namespace BD.Connectors.ClaudeCode.Options;

public abstract record ToolsConfig
{
    public sealed record Named(IReadOnlyList<string> Tools) : ToolsConfig;

    public sealed record ClaudeCodePreset : ToolsConfig;
}
