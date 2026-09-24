using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class OptionsConfiguratorTests
{
    [Test]
    [Arguments("Read", "Read")]
    [Arguments("mcp__server__tool", "mcp__server__tool")]
    [Arguments("Read(*)", "Read")]
    [Arguments("Read()", "Read")]
    [Arguments("mcp__server__tool(*)", "mcp__server__tool")]
    [Arguments("Bash(ls:*)", null)]
    [Arguments("Bash(git log:*)", null)]
    [Arguments("Bash(*.py)", null)]
    [Arguments("", null)]
    [Arguments("   ", null)]
    [Arguments("Bash(ls:*", null)]
    [Arguments("Bash(ls)x", null)]
    [Arguments("(foo)", null)]
    [Arguments("(*)", null)]
    [Arguments("Read(*x", null)]
    public async Task WholeToolAllowed_MatchesCliParser(string entry, string? expected)
    {
        await Assert.That(OptionsConfigurator.WholeToolAllowed(entry)).IsEqualTo(expected);
    }

    [Test]
    public async Task ShadowedWarning_BypassPermissions_MentionsReason()
    {
        var message = OptionsConfigurator.GetCanUseToolShadowedWarning(PermissionMode.BypassPermissions, []);

        await Assert.That(message).IsNotNull();
        await Assert.That(message).Contains("bypassPermissions");
        await Assert.That(message).Contains("PreToolUse");
    }

    [Test]
    public async Task ShadowedWarning_BareEntries_ListsOnlyWholeToolAllows()
    {
        var message = OptionsConfigurator.GetCanUseToolShadowedWarning(null, ["Read", "mcp__server__tool", "Bash(ls:*)"]);

        await Assert.That(message).IsNotNull();
        await Assert.That(message).Contains("Read, mcp__server__tool");
        await Assert.That(message).DoesNotContain("Bash(ls:*)");
    }

    [Test]
    public async Task ShadowedWarning_BypassPermissions_TakesPrecedenceOverEntries()
    {
        var message = OptionsConfigurator.GetCanUseToolShadowedWarning(PermissionMode.BypassPermissions, ["Read", "Write"]);

        await Assert.That(message).IsNotNull();
        await Assert.That(message).Contains("bypassPermissions");
        await Assert.That(message).DoesNotContain("Read");
    }

    [Test]
    public async Task ShadowedWarning_AcceptEditsWithoutBareEntries_ReturnsNull()
    {
        var message = OptionsConfigurator.GetCanUseToolShadowedWarning(PermissionMode.AcceptEdits, []);

        await Assert.That(message).IsNull();
    }

    [Test]
    public async Task ShadowedWarning_DedupsSameToolAndPreservesFirstSeenOrder()
    {
        var message = OptionsConfigurator.GetCanUseToolShadowedWarning(null, ["Write", "Read", "Write()"]);

        await Assert.That(message).IsNotNull();
        await Assert.That(message).Contains("invoked for: Write, Read.");
    }

    [Test]
    public async Task ShadowedWarning_SpecifiersOnly_ReturnsNull()
    {
        var message = OptionsConfigurator.GetCanUseToolShadowedWarning(null, ["Bash(ls:*)", "Bash(git log:*)", ""]);

        await Assert.That(message).IsNull();
    }

    [Test]
    public async Task ConfigureCanUseTool_NoCallback_ReturnsUnchanged()
    {
        var options = new ClaudeAgentOptions();

        var result = OptionsConfigurator.ConfigureCanUseTool(options);

        await Assert.That(result).IsEqualTo(options);
    }

    [Test]
    public async Task ConfigureCanUseTool_CallbackWithPermissionPromptToolName_Throws()
    {
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
            PermissionPromptToolName = "custom",
        };

        await Assert.That(() => OptionsConfigurator.ConfigureCanUseTool(options)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ConfigureCanUseTool_CallbackSet_SetsPermissionPromptToolNameToStdio()
    {
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };

        var result = OptionsConfigurator.ConfigureCanUseTool(options, NullLogger.Instance);

        await Assert.That(result.PermissionPromptToolName).IsEqualTo("stdio");
    }

    [Test]
    public async Task ConfigureCanUseTool_SkillsAll_ShadowsSkillTool()
    {
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
            Skills = new SkillsConfig.All(),
        };

        // Should not throw; the warning is logged, not raised. Behavior asserted via GetCanUseToolShadowedWarning directly below.
        _ = OptionsConfigurator.ConfigureCanUseTool(options, NullLogger.Instance);

        var message = OptionsConfigurator.GetCanUseToolShadowedWarning(options.PermissionMode, [.. options.AllowedTools, "Skill"]);
        await Assert.That(message).Contains("invoked for: Skill");
    }

    [Test]
    public async Task EnsureMcpConfigNotAmbiguous_BothSet_Throws()
    {
        var options = new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, McpServerConfig> { ["x"] = new McpStdioServerConfig("cmd") },
            McpConfig = "{}",
        };

        await Assert.That(() => OptionsConfigurator.EnsureMcpConfigNotAmbiguous(options)).Throws<ArgumentException>();
    }

    [Test]
    public async Task EnsureMcpConfigNotAmbiguous_OnlyOneSet_DoesNotThrow()
    {
        var options = new ClaudeAgentOptions { McpConfig = "{}" };

        OptionsConfigurator.EnsureMcpConfigNotAmbiguous(options);
    }
}
