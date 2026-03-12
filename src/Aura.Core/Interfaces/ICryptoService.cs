namespace Aura.Core.Interfaces;

public interface ICryptoService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}
