namespace AEngine.Core.Actions;

/// <summary>
/// Resolves handler ids to handler instances. Handlers can be registered
/// and replaced at runtime — the extensibility seam for custom handlers.
/// </summary>
public sealed class HandlerRegistry
{
    private readonly Dictionary<string, IActionHandler> _handlers = new(StringComparer.Ordinal);

    public IEnumerable<string> Ids => _handlers.Keys;

    public void Register(IActionHandler handler)
    {
        if (_handlers.ContainsKey(handler.Id))
            throw new InvalidOperationException($"Handler '{handler.Id}' is already registered.");
        _handlers[handler.Id] = handler;
    }

    /// <summary>Replace the handler for an id at runtime.</summary>
    public void Replace(IActionHandler handler) => _handlers[handler.Id] = handler;

    public IActionHandler Get(string id) =>
        _handlers.TryGetValue(id, out var handler)
            ? handler
            : throw new KeyNotFoundException($"No handler with id '{id}'.");

    public bool Has(string id) => _handlers.ContainsKey(id);
}
