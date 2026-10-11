namespace Aura.Infrastructure.Services;

/// <summary>
/// An essence the deployment parser rejects, with a message written for the user that names the layer.
/// Any other exception from the parser is a bug.
/// </summary>
public sealed class InvalidEssenceException : InvalidOperationException
{
    public InvalidEssenceException(string message) : base(message)
    {
    }
}
