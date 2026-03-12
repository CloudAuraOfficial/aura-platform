using System.Runtime.CompilerServices;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Aura.Api.Controllers;

namespace Aura.Tests;

public class LogStreamControllerTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly string _dbName;

    public LogStreamControllerTests()
    {
        _dbName = $"LogStreamTest_{Guid.NewGuid()}";
        var services = new ServiceCollection();

        services.AddDbContext<AuraDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(_dbName);
        });
        services.AddSingleton<ITenantContext>(new Aura.Worker.Services.WorkerTenantContext());
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    private AuraDbContext CreateDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AuraDbContext>();
    }

    [Fact]
    public async Task Stream_RunNotFound_Returns404()
    {
        using var db = CreateDb();
        var logStreamMock = new Mock<ILogStreamService>();

        var controller = new LogStreamController(db, logStreamMock.Object);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        await controller.Stream(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Stream_ValidRun_SetsSseHeaders()
    {
        var (tenantId, deploymentId, runId) = await SeedRunAsync();

        using var db = CreateDb();

        // Mock that yields two messages then completes
        var logStreamMock = new Mock<ILogStreamService>();
        logStreamMock
            .Setup(l => l.SubscribeAsync(runId, It.IsAny<CancellationToken>()))
            .Returns(YieldMessages("line 1", "line 2"));

        var controller = new LogStreamController(db, logStreamMock.Object);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        await controller.Stream(deploymentId, runId, CancellationToken.None);

        Assert.Equal("text/event-stream", httpContext.Response.ContentType);

        // Read the response body
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("data: line 1\n\n", body);
        Assert.Contains("data: line 2\n\n", body);
        Assert.Contains("data: [done]\n\n", body);
    }

    [Fact]
    public async Task Stream_ClientDisconnects_HandlesGracefully()
    {
        var (tenantId, deploymentId, runId) = await SeedRunAsync();

        using var db = CreateDb();

        // Mock that blocks indefinitely
        var logStreamMock = new Mock<ILogStreamService>();
        logStreamMock
            .Setup(l => l.SubscribeAsync(runId, It.IsAny<CancellationToken>()))
            .Returns(YieldForever());

        var controller = new LogStreamController(db, logStreamMock.Object);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Should not throw — gracefully handle cancellation
        await controller.Stream(deploymentId, runId, cts.Token);
    }

    private async Task<(Guid tenantId, Guid deploymentId, Guid runId)> SeedRunAsync()
    {
        using var db = CreateDb();

        // Use Guid.Empty as TenantId to match WorkerTenantContext's query filter
        var tenantId = Guid.Empty;
        var essenceId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.Essences.Add(new Essence
        {
            Id = essenceId,
            TenantId = tenantId,
            Name = "E",
            EssenceJson = "{\"layers\":{}}"
        });
        db.Deployments.Add(new Deployment
        {
            Id = deploymentId,
            TenantId = tenantId,
            EssenceId = essenceId,
            Name = "D",
            IsEnabled = true
        });
        db.DeploymentRuns.Add(new DeploymentRun
        {
            Id = runId,
            TenantId = tenantId,
            DeploymentId = deploymentId,
            Status = RunStatus.Running,
            SnapshotJson = "{}"
        });
        await db.SaveChangesAsync();

        return (tenantId, deploymentId, runId);
    }

    private static async IAsyncEnumerable<string> YieldMessages(
        params string[] messages)
    {
        foreach (var msg in messages)
        {
            yield return msg;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> YieldForever(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
            yield return "tick";
        }
    }
}
