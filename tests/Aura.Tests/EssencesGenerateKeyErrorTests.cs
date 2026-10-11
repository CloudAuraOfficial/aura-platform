using System.Security.Claims;
using System.Security.Cryptography;
using Aura.Api.Controllers;
using Aura.Api.Middleware;
using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class EssencesGenerateKeyErrorTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static EssencesController Controller(AuraDbContext db, Guid userId, Mock<ICryptoService> crypto)
    {
        var keySvc = new UserAiKeyService(db, crypto.Object, Mock.Of<ILogger<UserAiKeyService>>());
        var providers = new Mock<ILlmProviderFactory>();
        providers.Setup(f => f.SupportedProviders).Returns(new[] { "openrouter" });
        var aiBuilder = new AiEssenceBuilderService(
            providers.Object, keySvc, db, new EssenceValidator(), Mock.Of<ILogger<AiEssenceBuilderService>>());

        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Guid.Empty);

        var controller = new EssencesController(db, tenant.Object, Mock.Of<IAuditService>(), aiBuilder, new EssenceValidator(),
            Mock.Of<ILogger<EssencesController>>());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    // Reproduces gap G4 from #35: a stored key that can't be decrypted used to fall through to a generic
    // 500 internal_error. The controller no longer maps it itself. It lets the typed exception reach the
    // middleware, which owns the 422 (see ExceptionHandlerMiddlewareTests).
    [Fact]
    public async Task Generate_lets_stored_secret_unreadable_reach_the_middleware_with_the_provider_as_selected()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var cloudId = Guid.NewGuid();
        db.UserAiProviders.Add(new UserAiProvider
        {
            TenantId = Guid.Empty, UserId = userId, ProviderName = "openrouter", EncryptedApiKey = "from-another-secret"
        });
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = cloudId, TenantId = Guid.Empty, Provider = CloudProvider.Azure, Label = "acct"
        });
        await db.SaveChangesAsync();

        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(new CryptographicException()));
        var controller = Controller(db, userId, crypto);

        var ex = await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            controller.Generate(new GenerateEssenceRequest("build me a vnet", cloudId, "OpenRouter")));

        Assert.Equal("The stored key for 'OpenRouter' can't be read. Re-enter it in Account Settings.", ex.Message);
    }

    [Fact]
    public async Task Generate_returns_422_stored_secret_unreadable_through_the_middleware()
    {
        var db = CreateInMemoryDb();
        var userId = Guid.NewGuid();
        var cloudId = Guid.NewGuid();
        db.UserAiProviders.Add(new UserAiProvider
        {
            TenantId = Guid.Empty, UserId = userId, ProviderName = "openrouter", EncryptedApiKey = "from-another-secret"
        });
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = cloudId, TenantId = Guid.Empty, Provider = CloudProvider.Azure, Label = "acct"
        });
        await db.SaveChangesAsync();

        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(new CryptographicException()));
        var controller = Controller(db, userId, crypto);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlerMiddleware(
            async _ => await controller.Generate(new GenerateEssenceRequest("build me a vnet", cloudId, "OpenRouter")),
            Mock.Of<ILogger<ExceptionHandlerMiddleware>>());

        await middleware.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Equal(422, ctx.Response.StatusCode);
        Assert.Contains("\"error\":\"stored_secret_unreadable\"", json);
        Assert.Contains("The stored key for 'OpenRouter' can't be read. Re-enter it in Account Settings.", json);
        Assert.Contains("\"statusCode\":422", json);
    }
}
