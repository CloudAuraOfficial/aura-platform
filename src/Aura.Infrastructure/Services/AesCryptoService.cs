using System.Security.Cryptography;
using System.Text;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

public class AesCryptoService : ICryptoService
{
    private const int IvLength = 16;

    private readonly byte[] _key;

    public AesCryptoService(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (256-bit).");
    }

    public string Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
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

    private string DecryptOrThrow(string cipherText)
    {
        var fullCipher = Convert.FromBase64String(cipherText);
        if (fullCipher.Length <= IvLength)
            throw new CryptographicException("Stored value is too short to hold an IV and ciphertext.");

        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = fullCipher[..IvLength];
        var cipher = fullCipher[IvLength..];

        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
