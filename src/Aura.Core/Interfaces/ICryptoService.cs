using Aura.Core.Exceptions;

namespace Aura.Core.Interfaces;

public interface ICryptoService
{
    /// <summary>
    /// Encrypts a value in the current format. Always produces the current format, never a legacy one.
    /// </summary>
    string Encrypt(string plainText);

    /// <summary>
    /// Decrypts a stored value. Throws <see cref="StoredSecretUnreadableException"/> when the value
    /// cannot be decrypted, whatever the implementation's underlying failure.
    /// </summary>
    string Decrypt(string cipherText);

    /// <summary>
    /// True when a stored value is in an older format that should be re-encrypted with <see cref="Encrypt"/>
    /// after a successful read. Does not decrypt the value.
    /// </summary>
    bool IsLegacyFormat(string cipherText);
}
