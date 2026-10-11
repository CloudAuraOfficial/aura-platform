using System.Security.Cryptography;
using System.Text;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Encrypts stored secrets. Two formats are readable:
/// <list type="bullet">
/// <item><description><b>v2</b> (written by <see cref="Encrypt"/>): <c>"v2:" + base64(nonce | ciphertext | tag)</c>,
/// AES-256-GCM with a 12-byte random nonce and a 16-byte tag. Tampering or a wrong key fails authentication.</description></item>
/// <item><description><b>legacy</b> (no prefix, written before #45): base64(IV | AES-256-CBC ciphertext), PKCS7, no MAC.
/// Read only, so existing rows keep working until they are re-encrypted to v2.</description></item>
/// </list>
/// </summary>
public class AesCryptoService : ICryptoService
{
    private const string V2Prefix = "v2:";
    private const int LegacyIvLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] _key;

    public AesCryptoService(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (256-bit).");
    }

    public string Encrypt(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagLength];

        using (var gcm = new AesGcm(_key, TagLength))
        {
            gcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        var payload = new byte[NonceLength + cipherBytes.Length + TagLength];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceLength);
        Buffer.BlockCopy(cipherBytes, 0, payload, NonceLength, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, payload, NonceLength + cipherBytes.Length, TagLength);

        return V2Prefix + Convert.ToBase64String(payload);
    }

    public string Decrypt(string cipherText)
    {
        try
        {
            return DecryptOrThrow(cipherText);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Callers see only the typed exception. The inner exception is kept for logs.
            throw StoredSecretUnreadableException.FromCryptoFailure(ex);
        }
    }

    /// <inheritdoc />
    public bool IsLegacyFormat(string cipherText) => !cipherText.Contains(':');

    private string DecryptOrThrow(string cipherText)
    {
        if (cipherText.StartsWith(V2Prefix, StringComparison.Ordinal))
            return DecryptV2(cipherText[V2Prefix.Length..]);

        // base64 never contains ':', so a colon means a version this code does not know.
        if (!IsLegacyFormat(cipherText))
            throw new CryptographicException("Stored value uses an unknown encryption version.");

        return DecryptLegacyCbc(cipherText);
    }

    private string DecryptV2(string base64Payload)
    {
        var payload = Convert.FromBase64String(base64Payload);
        if (payload.Length < NonceLength + TagLength)
            throw new CryptographicException("Stored value is too short to hold a nonce and tag.");

        var nonce = payload[..NonceLength];
        var cipher = payload[NonceLength..^TagLength];
        var tag = payload[^TagLength..];
        var plainBytes = new byte[cipher.Length];

        using var gcm = new AesGcm(_key, TagLength);
        // Throws CryptographicException when the tag does not verify (tampering or a different key).
        gcm.Decrypt(nonce, cipher, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    // Unchanged from the pre-#45 format so existing rows still decrypt. Do not modify.
    private string DecryptLegacyCbc(string cipherText)
    {
        var fullCipher = Convert.FromBase64String(cipherText);
        if (fullCipher.Length <= LegacyIvLength)
            throw new CryptographicException("Stored value is too short to hold an IV and ciphertext.");

        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = fullCipher[..LegacyIvLength];
        var cipher = fullCipher[LegacyIvLength..];

        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
