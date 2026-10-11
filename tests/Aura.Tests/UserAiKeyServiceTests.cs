using System.Security.Cryptography;
using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Aura.Tests.SecretTestFixtures;

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
        return new UserAiKeyService(db, StoredSecrets(db, crypto.Object), Mock.Of<ILogger<UserAiKeyService>>());
    }

    private static async Task<Guid> SeedProvider(AuraDbContext db, string storedName, string ciphertext = "sk-secret")
    {
        var userId = Guid.NewGuid();
        db.UserAiProviders.Add(new UserAiProvider
        {
            TenantId = Guid.Empty, // matches the parameterless DbContext's tenant query filter
            UserId = userId,
            ProviderName = storedName,
            EncryptedApiKey = ciphertext
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

    // Keys encrypted under another secret fail in different ways: bad padding, a stored value
    // that isn't base64, or a value too short to hold the IV. The crypto layer reports all of them
    // as StoredSecretUnreadableException. This service names the provider and nothing else.
    [Theory]
    [InlineData(typeof(CryptographicException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    public async Task GetDecryptedKey_throws_provider_key_error_when_crypto_reports_unreadable(Type failure)
    {
        var db = CreateInMemoryDb();
        var userId = await SeedProvider(db, "openrouter");
        var inner = (Exception)Activator.CreateInstance(failure, "internal detail")!;
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(inner));
        var svc = new UserAiKeyService(db, StoredSecrets(db, crypto.Object), Mock.Of<ILogger<UserAiKeyService>>());

        var ex = await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            svc.GetDecryptedKeyAsync(userId, "OpenRouter"));

        Assert.Same(inner, ex.InnerException!.InnerException);
        Assert.Equal(
            "The stored key for 'OpenRouter' can't be read. Re-enter it in Account Settings.", ex.Message);
    }

    // The service handles only the typed exception. A raw failure from an implementation that breaks
    // the ICryptoService contract is a bug and must not be relabelled as a stored-key problem.
    [Fact]
    public async Task GetDecryptedKey_does_not_relabel_untyped_crypto_failures()
    {
        var db = CreateInMemoryDb();
        var userId = await SeedProvider(db, "openrouter");
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Throws(new CryptographicException());
        var svc = new UserAiKeyService(db, StoredSecrets(db, crypto.Object), Mock.Of<ILogger<UserAiKeyService>>());

        await Assert.ThrowsAsync<CryptographicException>(() =>
            svc.GetDecryptedKeyAsync(userId, "openrouter"));
    }

    [Fact]
    public async Task GetDecryptedKey_unreadable_message_and_log_never_contain_the_stored_ciphertext()
    {
        const string ciphertext = "CIPHERTEXT-MARKER-7f3a9c";
        var db = CreateInMemoryDb();
        var userId = await SeedProvider(db, "openrouter", ciphertext);
        var crypto = new Mock<ICryptoService>();
        // The implementation's own message echoes the stored value. The service must not pass it on.
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(
                new CryptographicException($"padding is invalid; value was {ciphertext}")));
        var logger = new Mock<ILogger<UserAiKeyService>>();
        var svc = new UserAiKeyService(db, StoredSecrets(db, crypto.Object), logger.Object);

        var ex = await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            svc.GetDecryptedKeyAsync(userId, "openrouter"));

        Assert.DoesNotContain(ciphertext, ex.Message);
        logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("provider=openrouter") &&
                    state.ToString()!.Contains("error=CryptographicException") &&
                    !state.ToString()!.Contains(ciphertext)),
                It.Is<Exception?>(e => e == null),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        logger.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(ciphertext)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
        logger.Verify(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception?>(e => e != null && e.ToString().Contains(ciphertext)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task GetDecryptedKey_does_not_decrypt_or_log_when_provider_not_configured()
    {
        var db = CreateInMemoryDb();
        var userId = await SeedProvider(db, "openai");
        var crypto = new Mock<ICryptoService>();
        var logger = new Mock<ILogger<UserAiKeyService>>();
        var svc = new UserAiKeyService(db, StoredSecrets(db, crypto.Object), logger.Object);

        Assert.Null(await svc.GetDecryptedKeyAsync(userId, "anthropic"));

        crypto.Verify(c => c.Decrypt(It.IsAny<string>()), Times.Never);
        logger.Verify(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }
}
