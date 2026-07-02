using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Aura.Tests;

public class UserAiKeyServiceTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static UserAiKeyService Service(AuraDbContext db)
    {
        // Identity crypto — we only care about lookup, not encryption here.
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Returns<string>(s => s);
        return new UserAiKeyService(db, crypto.Object);
    }

    private static async Task<Guid> SeedProvider(AuraDbContext db, string storedName)
    {
        var userId = Guid.NewGuid();
        db.UserAiProviders.Add(new UserAiProvider
        {
            TenantId = Guid.Empty, // matches the parameterless DbContext's tenant query filter
            UserId = userId,
            ProviderName = storedName,
            EncryptedApiKey = "sk-secret"
        });
        await db.SaveChangesAsync();
        return userId;
    }

    // #20: stored lower-case, looked up with the brand's natural casing → must still resolve.
    [Theory]
    [InlineData("openrouter")]
    [InlineData("OpenRouter")]
    [InlineData("OPENROUTER")]
    public async Task GetDecryptedKey_resolves_regardless_of_request_casing(string lookupName)
    {
        var db = CreateInMemoryDb();
        var userId = await SeedProvider(db, "openrouter");

        var key = await Service(db).GetDecryptedKeyAsync(userId, lookupName);

        Assert.Equal("sk-secret", key);
    }

    [Fact]
    public async Task GetDecryptedKey_returns_null_when_provider_not_configured()
    {
        var db = CreateInMemoryDb();
        var userId = await SeedProvider(db, "openai");

        Assert.Null(await Service(db).GetDecryptedKeyAsync(userId, "anthropic"));
    }
}
