namespace Aura.Core.Exceptions;

/// <summary>
/// A provider API key is stored for the user but cannot be decrypted, usually because it was
/// encrypted under a different secret. The user has to re-enter it. The message is user-facing.
/// </summary>
public sealed class StoredKeyUnreadableException : Exception
{
    public StoredKeyUnreadableException(string providerName)
        : base($"stored key for '{providerName}' can't be read, re-enter it in Account Settings.")
    {
        ProviderName = providerName;
    }

    public string ProviderName { get; }
}
