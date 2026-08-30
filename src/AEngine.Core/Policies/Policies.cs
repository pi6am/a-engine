using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Core.Policies;

/// <summary>
/// Decides what an autonomous agent does. Async by design: an LLM-backed
/// policy may take seconds, so TurnManager starts the selection, lets the
/// agent skip turns while it is in flight, and validates the choice
/// against the current world before executing it.
/// </summary>
public interface IAgentPolicy
{
    string Id { get; }

    /// <summary>
    /// Pick one of the currently resolved actions (or null to pass).
    /// For prompted verbs (e.g. say), supply the text via
    /// <see cref="AvailableAction.Text"/>.
    /// </summary>
    Task<AvailableAction?> ChooseActionAsync(
        GameEngine engine, WorldObject agent,
        IReadOnlyList<AvailableAction> actions, CancellationToken ct);

    /// <summary>
    /// Pick a reaction (option id) to a telegraphed action targeting this
    /// agent — a quick-time event. Null (the default implementation) = the
    /// reaction spec's default option.
    /// </summary>
    Task<string?> ChooseReactionAsync(
        GameEngine engine, WorldObject defender,
        PendingReaction reaction, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Resolves policy ids to policy instances. Policies can be registered
/// and replaced at runtime — the extensibility seam for smarter
/// (e.g. LLM-driven) agents, mirroring HandlerRegistry.
/// </summary>
public sealed class PolicyRegistry
{
    private readonly Dictionary<string, IAgentPolicy> _policies = new(StringComparer.Ordinal);

    public IEnumerable<string> Ids => _policies.Keys;

    public void Register(IAgentPolicy policy)
    {
        if (_policies.ContainsKey(policy.Id))
            throw new InvalidOperationException($"Policy '{policy.Id}' is already registered.");
        _policies[policy.Id] = policy;
    }

    /// <summary>Replace the policy for an id at runtime.</summary>
    public void Replace(IAgentPolicy policy) => _policies[policy.Id] = policy;

    public IAgentPolicy Get(string id) =>
        _policies.TryGetValue(id, out var policy)
            ? policy
            : throw new KeyNotFoundException($"No policy with id '{id}'.");

    public bool Has(string id) => _policies.ContainsKey(id);
}

/// <summary>
/// Built-in "random" policy: uniform pick over the resolved actions via
/// GameEngine.Random (settable, seedable in tests). Completes
/// synchronously but through the same async pipeline as any policy.
/// </summary>
public sealed class RandomPolicy : IAgentPolicy
{
    public string Id => "random";

    private static readonly string[] Phrases =
    [
        "Hm, where did I put it?",
        "Lovely weather, isn't it?",
        "Someone was rummaging around here.",
        "One moment, one moment...",
    ];

    public Task<AvailableAction?> ChooseActionAsync(
        GameEngine engine, WorldObject agent,
        IReadOnlyList<AvailableAction> actions, CancellationToken ct)
    {
        if (actions.Count == 0)
            return Task.FromResult<AvailableAction?>(null);
        var pick = actions[engine.Random.Next(actions.Count)];
        if (pick.Prompt is not null)
            pick = pick with { Text = Phrases[engine.Random.Next(Phrases.Length)] };
        return Task.FromResult<AvailableAction?>(pick);
    }

    /// <summary>Uniform pick among the reaction options.</summary>
    public Task<string?> ChooseReactionAsync(
        GameEngine engine, WorldObject defender,
        PendingReaction reaction, CancellationToken ct)
    {
        var option = reaction.Options[engine.Random.Next(reaction.Options.Count)];
        return Task.FromResult<string?>(option.Id);
    }
}

/// <summary>
/// Built-in "auto" policy: delegates to the "llm" policy when one is
/// registered (the CLI registers it when --llm-endpoint is set), else to
/// "random". Resolved per call, so registering/replacing the llm policy
/// at runtime takes effect immediately.
/// </summary>
public sealed class AutoPolicy : IAgentPolicy
{
    public string Id => "auto";

    public Task<AvailableAction?> ChooseActionAsync(
        GameEngine engine, WorldObject agent,
        IReadOnlyList<AvailableAction> actions, CancellationToken ct)
    {
        var inner = engine.PolicyRegistry.Get(
            engine.PolicyRegistry.Has("llm") ? "llm" : "random");
        return inner.ChooseActionAsync(engine, agent, actions, ct);
    }

    public Task<string?> ChooseReactionAsync(
        GameEngine engine, WorldObject defender,
        PendingReaction reaction, CancellationToken ct)
    {
        var inner = engine.PolicyRegistry.Get(
            engine.PolicyRegistry.Has("llm") ? "llm" : "random");
        return inner.ChooseReactionAsync(engine, defender, reaction, ct);
    }
}

