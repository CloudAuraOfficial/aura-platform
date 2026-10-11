namespace Aura.Core.Exceptions;

/// <summary>
/// A secret stored for the user (a provider API key or cloud credentials) is in the database but
/// cannot be decrypted, usually because it was encrypted under a different secret. The user has to
/// re-enter it.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is written for the user. It never contains the secret or its ciphertext.
/// <c>ICryptoService.Decrypt</c> throws this with the generic message. Callers that know what the
/// value is wrap it with <see cref="ForProviderKey"/> or <see cref="ForCloudAccount"/> so the message
/// names it. The underlying failure stays in <see cref="Exception.InnerException"/> for logs.
/// </remarks>
public sealed class StoredSecretUnreadableException : Exception
{
    private const string GenericMessage = "A stored secret can't be read. Re-enter it.";

    private StoredSecretUnreadableException(string message, Exception? inner) : base(message, inner)
    {
    }

    public static StoredSecretUnreadableException FromCryptoFailure(Exception inner) =>
        new(GenericMessage, inner);

    public static StoredSecretUnreadableException ForProviderKey(string providerName, Exception? inner = null) =>
        new($"The stored key for '{providerName}' can't be read. Re-enter it in Account Settings.", inner);

    public static StoredSecretUnreadableException ForCloudAccount(string label, Exception? inner = null) =>
        new($"The stored credentials for '{label}' can't be read. Re-enter them in Cloud Accounts.", inner);
}
