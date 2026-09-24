namespace BD.Connectors.ClaudeCode.Options;

// null (ClaudeAgentOptions.SystemPrompt) means an empty system prompt (--system-prompt ""), like PY.
public abstract record SystemPromptConfig
{
    public sealed record Text(string Value) : SystemPromptConfig;

    public sealed record Preset(string? Append = null, bool? ExcludeDynamicSections = null, bool? Snapshot = null) : SystemPromptConfig;

    public sealed record Custom(string Prompt, bool? Snapshot = null) : SystemPromptConfig;

    public sealed record File(string Path) : SystemPromptConfig;
}
