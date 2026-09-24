namespace BD.Connectors.ClaudeCode.Tests.Shared;

// For TestKind=CliSmoke: a spawnable claude CLI is needed, but no login.
internal sealed class RequiresClaudeCliAttribute : TUnit.Core.SkipAttribute
{
    private const string SkipMessage = "Requires a locally discoverable, spawnable Claude Code CLI.";

    public RequiresClaudeCliAttribute()
        : base(SkipMessage)
    {
    }

    public override Task<bool> ShouldSkip(TUnit.Core.TestRegisteredContext testContext)
    {
        _ = testContext;
        return Task.FromResult(!RealClaudeTestSupport.CanRunCliSmokeTests());
    }
}
