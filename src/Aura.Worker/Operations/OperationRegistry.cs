namespace Aura.Worker.Operations;

public class OperationRegistry
{
    private readonly Dictionary<string, Type> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void Register<THandler>(string operationType) where THandler : IOperationHandler
    {
        _handlers[operationType] = typeof(THandler);
    }

    public IOperationHandler Resolve(IServiceProvider sp, string operationType)
    {
        if (!_handlers.TryGetValue(operationType, out var handlerType))
            throw new InvalidOperationException(
                $"No handler registered for operation type '{operationType}'.");

        return (IOperationHandler)sp.GetRequiredService(handlerType);
    }
}
