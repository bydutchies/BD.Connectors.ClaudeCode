namespace BD.Connectors.ClaudeCode.Options;

// "type" is currently always "local"; CliCommandBuilder (F2) rejects anything else.
public sealed record SdkPluginConfig(string Type, string Path);
