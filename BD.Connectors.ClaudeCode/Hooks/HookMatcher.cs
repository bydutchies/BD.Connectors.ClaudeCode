namespace BD.Connectors.ClaudeCode.Hooks;

public sealed record HookMatcher(string? Matcher, IReadOnlyList<HookCallback> Hooks, double? TimeoutSeconds = null);

public sealed record HookContext
{
    public CancellationToken CancellationToken { get; init; }
}

public delegate Task<HookJsonOutput> HookCallback(HookInput input, string? toolUseId, HookContext context);
