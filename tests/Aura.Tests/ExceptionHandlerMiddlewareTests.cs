using System.Security.Cryptography;
using System.Text.Json;
using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class ExceptionHandlerMiddlewareTests
{
    private static async Task<(DefaultHttpContext ctx, ErrorResponse body)> Invoke(RequestDelegate next)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlerMiddleware(next, Mock.Of<ILogger<ExceptionHandlerMiddleware>>());

        await middleware.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<ErrorResponse>(ctx.Response.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return (ctx, body!);
    }

    [Fact]
    public async Task Maps_StoredSecretUnreadable_to_422_in_the_standard_shape()
    {
        var failure = StoredSecretUnreadableException.ForProviderKey("OpenRouter",
            new CryptographicException("internal detail"));

        var (ctx, body) = await Invoke(_ => throw failure);

        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.StartsWith("application/json", ctx.Response.ContentType);
        Assert.Equal("stored_secret_unreadable", body.Error);
        Assert.Equal("The stored key for 'OpenRouter' can't be read. Re-enter it in Account Settings.", body.Message);
        Assert.Equal(422, body.StatusCode);
    }

    [Fact]
    public async Task Maps_cloud_account_failure_to_422_with_the_account_label()
    {
        var failure = StoredSecretUnreadableException.ForCloudAccount("prod-azure",
            new CryptographicException("internal detail"));

        var (ctx, body) = await Invoke(_ => throw failure);

        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Equal("The stored credentials for 'prod-azure' can't be read. Re-enter them in Cloud Accounts.", body.Message);
    }

    [Fact]
    public async Task Untyped_failures_stay_500_internal_error()
    {
        // Regression guard: only the typed exception is a user-state problem. Anything else is a bug.
        var (ctx, body) = await Invoke(_ => throw new CryptographicException("boom"));

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Equal("internal_error", body.Error);
        Assert.DoesNotContain("boom", body.Message);
    }

    [Fact]
    public async Task Successful_requests_pass_through_untouched()
    {
        var ctx = new DefaultHttpContext();
        var middleware = new ExceptionHandlerMiddleware(
            c => { c.Response.StatusCode = 204; return Task.CompletedTask; },
            Mock.Of<ILogger<ExceptionHandlerMiddleware>>());

        await middleware.InvokeAsync(ctx);

        Assert.Equal(204, ctx.Response.StatusCode);
    }
}
