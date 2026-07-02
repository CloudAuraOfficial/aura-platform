using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class AiEssenceBuilderServiceTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    // A provider stub that reports success with fixed token usage but returns a body
    // that never parses to valid essence JSON — so every iteration "spends" tokens.
    private sealed class TokenBurningProvider : ILlmProvider
    {
        public string ProviderName => "openrouter";
        public Task<LlmCompletionResult> GenerateAsync(LlmRequest request, CancellationToken ct = default)
            => Task.FromResult(new LlmCompletionResult("not json", 10, 5, "test-model", true));
    }

    private sealed class StubFactory : ILlmProviderFactory
    {
        private readonly ILlmProvider _p;
        public StubFactory(ILlmProvider p) => _p = p;
        public ILlmProvider GetProvider(string providerName) => _p;
        public IReadOnlyList<string> SupportedProviders => new[] { _p.ProviderName };
    }

    private static async Task<(AiEssenceBuilderService svc, AuraDbContext db, Guid userId, Guid tenantId, Guid cloudId)>
        Setup(AuraDbContext db)
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.Empty; // matches the parameterless DbContext's tenant query filter
        var cloudId = Guid.NewGuid();

        db.UserAiProviders.Add(new UserAiProvider
        {
            TenantId = tenantId, UserId = userId, ProviderName = "openrouter", EncryptedApiKey = "sk-secret"
        });
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = cloudId, TenantId = tenantId, Provider = CloudProvider.Azure, Label = "acct"
        });
        await db.SaveChangesAsync();

        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Returns<string>(s => s);
        var keySvc = new UserAiKeyService(db, crypto.Object);
        var svc = new AiEssenceBuilderService(
            new StubFactory(new TokenBurningProvider()), keySvc, db,
            Mock.Of<ILogger<AiEssenceBuilderService>>());
        return (svc, db, userId, tenantId, cloudId);
    }

    // #21: a generation that exhausts all retries must still record its token usage
    // (paid usage) in AiGenerationLog with Success=false, not lose it when it throws.
    [Fact]
    public async Task GenerateAsync_logs_usage_with_Success_false_when_all_iterations_fail()
    {
        var db = CreateInMemoryDb();
        var (svc, _, userId, tenantId, cloudId) = await Setup(db);
        var request = new GenerateEssenceRequest("build me a vnet", cloudId, "openrouter");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.GenerateAsync(userId, request, tenantId, CancellationToken.None));

        var log = await db.Set<AiGenerationLog>().SingleAsync();
        Assert.False(log.Success);
        Assert.Equal(3, log.Iterations);            // MaxIterations
        Assert.Equal(30, log.InputTokens);          // 10 × 3 iterations
        Assert.Equal(15, log.OutputTokens);         // 5 × 3 iterations
        Assert.Equal("openrouter", log.ProviderName);
    }
}
