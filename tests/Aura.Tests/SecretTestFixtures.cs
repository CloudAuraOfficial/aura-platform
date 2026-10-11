using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Aura.Tests;

/// <summary>
/// Key, legacy fixture and database helpers shared by the stored-secret tests.
/// </summary>
internal static class SecretTestFixtures
{
    /// <summary>Test key: bytes 1..32. Never used outside tests.</summary>
    public static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    /// <summary>
    /// Pre-#45 AES-256-CBC value (no prefix), made with <c>openssl enc -aes-256-cbc</c>, key = bytes 1..32,
    /// IV = 00 11 .. ff, PKCS7. Stored as base64(IV | ciphertext). It must keep decrypting.
    /// </summary>
    public const string LegacyCbcValue = "ABEiM0RVZneImaq7zN3u/84hAm9he171SbwDijRUtv6tsvTwTEs1I5PSbdos/wTe";
    public const string LegacyCbcPlainText = "sk-legacy-cbc-456";

    public static AesCryptoService Crypto() => new(Key);

    /// <summary>In-memory context. Enough for reads and plain saves. It does not support ExecuteUpdate.</summary>
    public static AuraDbContext InMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    /// <summary>
    /// Relational context on SQLite in memory, schema created. The conditional upgrade uses ExecuteUpdate, which
    /// the in-memory provider does not support. The caller disposes the connection.
    /// </summary>
    public static (AuraDbContext Db, SqliteConnection Connection) SqliteDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        // Seed rows use Guid.Empty as tenant and random user ids, as the in-memory tests do. Foreign keys are not under test.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AuraDbContext(options);
        db.Database.EnsureCreated();
        return (db, connection);
    }

    public static StoredSecretService StoredSecrets(AuraDbContext db, ICryptoService crypto, bool upgradeLegacy = true) =>
        new(db, crypto, new StoredSecretOptions { UpgradeLegacyOnRead = upgradeLegacy }, Mock.Of<ILogger<StoredSecretService>>());

    public static ILogger<T> Log<T>() => Mock.Of<ILogger<T>>();
}

/// <summary>
/// Wraps a real crypto service. It counts Decrypt calls and can run an action before Encrypt. That lets a test
/// change a row between the read and the conditional write, the way a concurrent user edit would.
/// </summary>
internal sealed class SpyCrypto : ICryptoService
{
    private readonly ICryptoService _inner;
    private readonly Action? _beforeEncrypt;

    public SpyCrypto(ICryptoService inner, Action? beforeEncrypt = null)
    {
        _inner = inner;
        _beforeEncrypt = beforeEncrypt;
    }

    public List<string> DecryptedInputs { get; } = new();

    public string Encrypt(string plainText)
    {
        _beforeEncrypt?.Invoke();
        return _inner.Encrypt(plainText);
    }

    public string Decrypt(string cipherText)
    {
        DecryptedInputs.Add(cipherText);
        return _inner.Decrypt(cipherText);
    }

    public bool IsLegacyFormat(string cipherText) => _inner.IsLegacyFormat(cipherText);
}

/// <summary>Migration lock stand-in. Reports whether it was acquired and counts releases.</summary>
internal sealed class FakeMigrationLock : ISecretMigrationLock
{
    private readonly bool _acquire;

    public FakeMigrationLock(bool acquire = true)
    {
        _acquire = acquire;
    }

    public int Releases { get; private set; }

    public Task<bool> TryAcquireAsync(CancellationToken ct) => Task.FromResult(_acquire);

    public Task ReleaseAsync(CancellationToken ct)
    {
        Releases++;
        return Task.CompletedTask;
    }
}
