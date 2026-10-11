using System.Security.Cryptography;
using Aura.Core.Exceptions;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

public class AesCryptoServiceTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    // Produced by the pre-#45 AES-256-CBC implementation (openssl enc -aes-256-cbc, key = bytes 1..32,
    // IV = 00 11 .. ff, PKCS7), stored as base64(IV | ciphertext). Plaintext: "sk-legacy-cbc-456".
    private const string LegacyCbcValue = "ABEiM0RVZneImaq7zN3u/84hAm9he171SbwDijRUtv6tsvTwTEs1I5PSbdos/wTe";
    private const string LegacyCbcPlainText = "sk-legacy-cbc-456";

    [Fact]
    public void Decrypt_round_trips_a_value_encrypted_with_the_same_key()
    {
        var crypto = new AesCryptoService(Key);

        Assert.Equal("sk-live-123", crypto.Decrypt(crypto.Encrypt("sk-live-123")));
    }

    [Fact]
    public void Encrypt_writes_the_v2_format_with_a_12_byte_nonce_and_16_byte_tag()
    {
        var crypto = new AesCryptoService(Key);

        var stored = crypto.Encrypt("abc");

        Assert.StartsWith("v2:", stored);
        // nonce (12) + ciphertext (3) + tag (16)
        Assert.Equal(12 + 3 + 16, Convert.FromBase64String(stored["v2:".Length..]).Length);
        Assert.False(crypto.IsLegacyFormat(stored));
    }

    [Fact]
    public void Encrypt_uses_a_fresh_nonce_each_time()
    {
        var crypto = new AesCryptoService(Key);

        Assert.NotEqual(crypto.Encrypt("same"), crypto.Encrypt("same"));
    }

    [Fact]
    public void Round_trip_handles_an_empty_value()
    {
        var crypto = new AesCryptoService(Key);

        Assert.Equal(string.Empty, crypto.Decrypt(crypto.Encrypt(string.Empty)));
    }

    [Fact]
    public void Decrypt_reads_a_legacy_cbc_value_written_by_the_old_algorithm()
    {
        var crypto = new AesCryptoService(Key);

        Assert.Equal(LegacyCbcPlainText, crypto.Decrypt(LegacyCbcValue));
    }

    [Fact]
    public void IsLegacyFormat_is_true_for_unprefixed_values_and_false_for_v2()
    {
        var crypto = new AesCryptoService(Key);

        Assert.True(crypto.IsLegacyFormat(LegacyCbcValue));
        Assert.False(crypto.IsLegacyFormat(crypto.Encrypt("x")));
    }

    [Theory]
    [InlineData(0)]   // nonce byte
    [InlineData(12)]  // first ciphertext byte
    [InlineData(-1)]  // last tag byte
    public void Decrypt_v2_fails_typed_when_any_byte_of_nonce_ciphertext_or_tag_is_flipped(int offset)
    {
        var crypto = new AesCryptoService(Key);
        var stored = crypto.Encrypt("sk-live-123456");
        var payload = Convert.FromBase64String(stored["v2:".Length..]);
        var index = offset < 0 ? payload.Length + offset : offset;
        payload[index] ^= 0x01;
        var tampered = "v2:" + Convert.ToBase64String(payload);

        var ex = Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt(tampered));

        Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void Decrypt_v2_fails_typed_when_a_ciphertext_byte_is_flipped_in_the_middle()
    {
        var crypto = new AesCryptoService(Key);
        var stored = crypto.Encrypt("sk-live-123456789");
        var payload = Convert.FromBase64String(stored["v2:".Length..]);
        payload[payload.Length / 2] ^= 0x80;
        var tampered = "v2:" + Convert.ToBase64String(payload);

        Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt(tampered));
    }

    [Fact]
    public void Decrypt_v2_with_a_wrong_key_always_fails_over_1000_random_keys()
    {
        var stored = new AesCryptoService(Key).Encrypt("sk-live-123");
        var successes = 0;

        for (var i = 0; i < 1000; i++)
        {
            var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var other = new AesCryptoService(otherKey);
            try
            {
                other.Decrypt(stored);
                successes++;
            }
            catch (StoredSecretUnreadableException)
            {
            }
        }

        Assert.Equal(0, successes);
    }

    [Fact]
    public void Decrypt_v2_fails_typed_when_the_payload_is_shorter_than_nonce_plus_tag()
    {
        var crypto = new AesCryptoService(Key);
        var tooShort = "v2:" + Convert.ToBase64String(new byte[27]);

        var ex = Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt(tooShort));

        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void Decrypt_fails_typed_for_an_unknown_version_prefix()
    {
        var crypto = new AesCryptoService(Key);

        Assert.Throws<StoredSecretUnreadableException>(() => crypto.Decrypt("v9:" + LegacyCbcValue));
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
