namespace BD.Connectors.ClaudeCode.Options;

public abstract record ThinkingConfig
{
    public sealed record Adaptive(ThinkingDisplay? Display = null) : ThinkingConfig;

    public sealed record Enabled(int BudgetTokens, ThinkingDisplay? Display = null) : ThinkingConfig;

    public sealed record Disabled : ThinkingConfig;
}
