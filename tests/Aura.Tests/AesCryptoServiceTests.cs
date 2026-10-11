using System.Security.Cryptography;
using Aura.Core.Exceptions;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

public class AesCryptoServiceTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void Decrypt_round_trips_a_value_encrypted_with_the_same_key()
    {
        var crypto = new AesCryptoService(Key);

        Assert.Equal("sk-live-123", crypto.Decrypt(crypto.Encrypt("sk-live-123")));
    }

    [Fact]
    public void Decrypt_throws_typed_exception_when_value_is_not_base64()
    {
        var crypto = new AesCryptoService(Key);

        var ex = Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt("not base64!!"));

        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Decrypt_throws_typed_exception_when_value_is_too_short_to_hold_an_iv()
    {
        var crypto = new AesCryptoService(Key);
        var tooShort = Convert.ToBase64String(new byte[16]); // IV only, no ciphertext

        var ex = Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt(tooShort));

        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void Decrypt_throws_typed_exception_when_ciphertext_is_not_a_whole_block()
    {
        // Deterministic failure under CBC: a body that is not a multiple of the block size. Wrong-key
        // decryption can succeed by chance, so it is not used here (see #45).
        var crypto = new AesCryptoService(Key);
        var malformed = Convert.ToBase64String(new byte[16 + 5]);

        var ex = Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt(malformed));

        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void Decrypt_unreadable_message_never_contains_the_ciphertext()
    {
        var crypto = new AesCryptoService(Key);
        var malformed = Convert.ToBase64String(new byte[16 + 5]);

        var ex = Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt(malformed));

        Assert.DoesNotContain(malformed, ex.Message);
    }

    [Fact]
    public void Constructor_keeps_argument_exception_for_a_wrong_key_length()
    {
        // A bad configured key is a startup error, not a stored value. It must not be relabelled.
        var shortKey = Convert.ToBase64String(new byte[16]);

        Assert.Throws<ArgumentException>(() => new AesCryptoService(shortKey));
    }
}
