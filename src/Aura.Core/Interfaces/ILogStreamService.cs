namespace Aura.Core.Interfaces;

public interface ILogStreamService
{
    Task PublishAsync(Guid runId, string message, CancellationToken ct = default);
    IAsyncEnumerable<string> SubscribeAsync(Guid runId, CancellationToken ct = default);
}
