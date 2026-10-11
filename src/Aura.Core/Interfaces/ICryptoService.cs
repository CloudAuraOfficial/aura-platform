using Aura.Core.Exceptions;

namespace Aura.Core.Interfaces;

public interface ICryptoService
{
    string Encrypt(string plainText);

    /// <summary>
    /// Decrypts a stored value. Throws <see cref="StoredSecretUnreadableException"/> when the value
    /// cannot be decrypted, whatever the implementation's underlying failure.
    /// </summary>
    string Decrypt(string cipherText);
}
