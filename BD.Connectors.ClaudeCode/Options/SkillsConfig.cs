namespace BD.Connectors.ClaudeCode.Options;

public abstract record SkillsConfig
{
    public sealed record All : SkillsConfig;

    public sealed record Named(IReadOnlyList<string> Skills) : SkillsConfig;
}
