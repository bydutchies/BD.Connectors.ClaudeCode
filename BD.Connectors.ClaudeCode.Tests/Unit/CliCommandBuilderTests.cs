using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

internal sealed class FakeSdkMcpServer : ISdkMcpServer
{
    public string Name => "fake";

    public Task<JsonObject?> HandleAsync(JsonObject jsonRpcMessage, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[Property("TestKind", "Unit")]
public class CliCommandBuilderTests
{
    [Test]
    public async Task Build_Defaults_ProducesBaseFlagsAndEmptySystemPrompt()
    {
        var args = CliCommandBuilder.Build("claude", new ClaudeAgentOptions(), isWindows: false);

        await Assert.That(args[0]).IsEqualTo("--output-format");
        await Assert.That(args[1]).IsEqualTo("stream-json");
        await Assert.That(args[2]).IsEqualTo("--verbose");
        await Assert.That(args).Contains("--system-prompt");
        await Assert.That(IndexAfter(args, "--system-prompt")).IsEqualTo(string.Empty);
        // Always ends with --input-format stream-json (streaming mode, matching the TypeScript SDK).
        await Assert.That(args[^2]).IsEqualTo("--input-format");
        await Assert.That(args[^1]).IsEqualTo("stream-json");
    }

    [Test]
    public async Task Build_SystemPromptText_PassesThrough()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = new SystemPromptConfig.Text("Be helpful") };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--system-prompt")).IsEqualTo("Be helpful");
    }

    [Test]
    public async Task Build_SystemPromptFile_UsesSystemPromptFileFlag()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = new SystemPromptConfig.File("/tmp/prompt.txt") };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--system-prompt-file")).IsEqualTo("/tmp/prompt.txt");
        await Assert.That(args).DoesNotContain("--system-prompt");
    }

    [Test]
    public async Task Build_SystemPromptPresetWithoutAppend_AddsNoFlag()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = new SystemPromptConfig.Preset() };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).DoesNotContain("--system-prompt");
        await Assert.That(args).DoesNotContain("--append-system-prompt");
    }

    [Test]
    public async Task Build_SystemPromptPresetWithAppend_UsesAppendFlag()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = new SystemPromptConfig.Preset(Append: "extra guidance") };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--append-system-prompt")).IsEqualTo("extra guidance");
    }

    [Test]
    public async Task Build_ToolsEmptyList_PassesEmptyString()
    {
        var options = new ClaudeAgentOptions { Tools = new ToolsConfig.Named([]) };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--tools")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Build_ToolsNamedList_JoinsWithComma()
    {
        var options = new ClaudeAgentOptions { Tools = new ToolsConfig.Named(["Read", "Write"]) };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--tools")).IsEqualTo("Read,Write");
    }

    [Test]
    public async Task Build_ToolsPreset_MapsToDefault()
    {
        var options = new ClaudeAgentOptions { Tools = new ToolsConfig.ClaudeCodePreset() };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--tools")).IsEqualTo("default");
    }

    [Test]
    public async Task Build_SkillsAll_InjectsBareSkillToolAndDefaultSettingSources()
    {
        var options = new ClaudeAgentOptions { Skills = new SkillsConfig.All() };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--allowedTools")).IsEqualTo("Skill");
        await Assert.That(args).Contains("--setting-sources=user,project");
    }

    [Test]
    public async Task Build_SkillsNamed_InjectsSkillRulePerName()
    {
        var options = new ClaudeAgentOptions { Skills = new SkillsConfig.Named(["pdf-fill", "commit-helper"]) };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--allowedTools")).IsEqualTo("Skill(pdf-fill),Skill(commit-helper)");
    }

    [Test]
    public async Task Build_SkillsNamed_InvalidName_Throws()
    {
        var options = new ClaudeAgentOptions { Skills = new SkillsConfig.Named(["bad(name)"]) };

        var action = () => CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(action).Throws<ArgumentException>();
    }

    [Test]
    public async Task Build_ExplicitSettingSources_NotOverriddenBySkillsDefault()
    {
        var options = new ClaudeAgentOptions { Skills = new SkillsConfig.All(), SettingSources = [SettingSource.Local] };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--setting-sources=local");
    }

    [Test]
    public async Task Build_MaxTurnsZero_IsOmitted()
    {
        var options = new ClaudeAgentOptions { MaxTurns = 0 };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).DoesNotContain("--max-turns");
    }

    [Test]
    public async Task Build_MaxTurnsSet_IsIncluded()
    {
        var options = new ClaudeAgentOptions { MaxTurns = 5 };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--max-turns")).IsEqualTo("5");
    }

    [Test]
    public async Task Build_Resume_UsesEqualsForm()
    {
        var options = new ClaudeAgentOptions { Resume = "session-123" };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--resume=session-123");
        // Two-token form must never appear: it would let a dash-leading value inject a flag.
        await Assert.That(args).DoesNotContain("--resume");
    }

    [Test]
    public async Task Build_Resume_OnWindowsWithMetacharacter_Throws()
    {
        var options = new ClaudeAgentOptions { Resume = "session&title" };

        var action = () => CliCommandBuilder.Build("claude", options, isWindows: true);

        await Assert.That(action).Throws<ArgumentException>();
    }

    [Test]
    public async Task Build_Resume_OffWindowsWithMetacharacter_DoesNotThrow()
    {
        var options = new ClaudeAgentOptions { Resume = "session&title" };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--resume=session&title");
    }

    [Test]
    public async Task Build_SessionId_UsesEqualsForm()
    {
        var options = new ClaudeAgentOptions { SessionId = "abc-123" };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--session-id=abc-123");
    }

    [Test]
    public async Task Build_ResumeDropsTurnEmptyString_IsStillForwarded()
    {
        // `is not null`, not truthiness: an empty string must still reach the CLI so it rejects the
        // malformed declaration, rather than the SDK silently disarming the guard.
        var options = new ClaudeAgentOptions { ResumeDropsTurn = string.Empty };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--resume-drops-turn=");
    }

    [Test]
    public async Task Build_SettingsOnly_PassedThroughAsIs()
    {
        var options = new ClaudeAgentOptions { Settings = "/etc/claude/settings.json" };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--settings")).IsEqualTo("/etc/claude/settings.json");
    }

    [Test]
    public async Task Build_SandboxOnly_MergesIntoSettingsJson()
    {
        var options = new ClaudeAgentOptions { Sandbox = new SandboxSettings { Enabled = true } };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        var settingsJson = IndexAfter(args, "--settings");
        var parsed = JsonNode.Parse(settingsJson!)!.AsObject();
        await Assert.That(parsed["sandbox"]!["enabled"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Build_SettingsJsonStringAndSandbox_AreMerged()
    {
        var options = new ClaudeAgentOptions
        {
            Settings = "{\"model\":\"sonnet\"}",
            Sandbox = new SandboxSettings { Enabled = true },
        };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        var parsed = JsonNode.Parse(IndexAfter(args, "--settings")!)!.AsObject();
        await Assert.That(parsed["model"]!.GetValue<string>()).IsEqualTo("sonnet");
        await Assert.That(parsed["sandbox"]!["enabled"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Build_McpServersDict_SerializesUnderMcpServersKey_ExcludingSdkInstance()
    {
        var sdkServer = new FakeSdkMcpServer();
        var options = new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["files"] = new McpStdioServerConfig("node", ["server.js"]),
                ["inproc"] = new McpSdkServerConfig("inproc", sdkServer),
            },
        };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        var parsed = JsonNode.Parse(IndexAfter(args, "--mcp-config")!)!.AsObject();
        var servers = parsed["mcpServers"]!.AsObject();
        await Assert.That(servers["files"]!["command"]!.GetValue<string>()).IsEqualTo("node");
        await Assert.That(servers["inproc"]!["type"]!.GetValue<string>()).IsEqualTo("sdk");
        await Assert.That(servers["inproc"]!.AsObject().ContainsKey("instance")).IsFalse();
    }

    [Test]
    public async Task Build_McpConfigString_PassedThroughDirectly()
    {
        var options = new ClaudeAgentOptions { McpConfig = "/path/to/mcp.json" };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--mcp-config")).IsEqualTo("/path/to/mcp.json");
    }

    [Test]
    public async Task Build_Plugins_LocalType_AddsPluginDirPerEntry()
    {
        var options = new ClaudeAgentOptions { Plugins = [new SdkPluginConfig("local", "/plugins/a"), new SdkPluginConfig("local", "/plugins/b")] };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        var pluginDirIndexes = args.Select((a, i) => (a, i)).Where(t => t.a == "--plugin-dir").Select(t => t.i).ToList();
        await Assert.That(pluginDirIndexes.Count).IsEqualTo(2);
        await Assert.That(args[pluginDirIndexes[0] + 1]).IsEqualTo("/plugins/a");
        await Assert.That(args[pluginDirIndexes[1] + 1]).IsEqualTo("/plugins/b");
    }

    [Test]
    public async Task Build_Plugins_UnsupportedType_Throws()
    {
        var options = new ClaudeAgentOptions { Plugins = [new SdkPluginConfig("git", "/plugins/a")] };

        var action = () => CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(action).Throws<ArgumentException>();
    }

    [Test]
    public async Task Build_ExtraArgs_NullValue_IsBooleanFlag()
    {
        var options = new ClaudeAgentOptions { ExtraArgs = new Dictionary<string, string?> { ["debug-foo"] = null } };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--debug-foo");
    }

    [Test]
    public async Task Build_ExtraArgs_DashLeadingValue_UsesEqualsForm()
    {
        var options = new ClaudeAgentOptions { ExtraArgs = new Dictionary<string, string?> { ["some-flag"] = "-1" } };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--some-flag=-1");
    }

    [Test]
    public async Task Build_ExtraArgs_OrdinaryValue_UsesTwoTokenForm()
    {
        var options = new ClaudeAgentOptions { ExtraArgs = new Dictionary<string, string?> { ["some-flag"] = "value" } };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--some-flag")).IsEqualTo("value");
    }

    [Test]
    public async Task Build_ThinkingAdaptiveWithDisplay_AddsBothFlags()
    {
        var options = new ClaudeAgentOptions { Thinking = new ThinkingConfig.Adaptive(ThinkingDisplay.Summarized) };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--thinking")).IsEqualTo("adaptive");
        await Assert.That(IndexAfter(args, "--thinking-display")).IsEqualTo("summarized");
    }

    [Test]
    public async Task Build_ThinkingEnabled_UsesMaxThinkingTokensFlag()
    {
        var options = new ClaudeAgentOptions { Thinking = new ThinkingConfig.Enabled(4096) };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--max-thinking-tokens")).IsEqualTo("4096");
        await Assert.That(args).DoesNotContain("--thinking");
    }

    [Test]
    public async Task Build_ThinkingDisabled_NoDisplayFlag()
    {
        var options = new ClaudeAgentOptions { Thinking = new ThinkingConfig.Disabled() };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--thinking")).IsEqualTo("disabled");
        await Assert.That(args).DoesNotContain("--thinking-display");
    }

    [Test]
    public async Task Build_ThinkingTakesPrecedenceOverDeprecatedMaxThinkingTokens()
    {
        var options = new ClaudeAgentOptions { Thinking = new ThinkingConfig.Disabled(), MaxThinkingTokens = 999 };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).DoesNotContain("--max-thinking-tokens");
    }

    [Test]
    public async Task Build_DeprecatedMaxThinkingTokens_UsedWhenThinkingUnset()
    {
        var options = new ClaudeAgentOptions { MaxThinkingTokens = 777 };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--max-thinking-tokens")).IsEqualTo("777");
    }

    [Test]
    public async Task Build_Effort_AddsEffortFlagWithWireValue()
    {
        var options = new ClaudeAgentOptions { Effort = EffortLevel.XHigh };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--effort")).IsEqualTo("xhigh");
    }

    [Test]
    public async Task Build_OutputFormatJsonSchema_AddsJsonSchemaFlag()
    {
        var schema = new JsonObject { ["type"] = "object" };
        var options = new ClaudeAgentOptions { OutputFormat = OutputFormats.JsonSchema(schema) };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        var parsed = JsonNode.Parse(IndexAfter(args, "--json-schema")!)!.AsObject();
        await Assert.That(parsed["type"]!.GetValue<string>()).IsEqualTo("object");
    }

    [Test]
    public async Task Build_BooleanFlags_OnlyAddedWhenSet()
    {
        var options = new ClaudeAgentOptions
        {
            IncludePartialMessages = true,
            IncludeHookEvents = true,
            StrictMcpConfig = true,
            ForkSession = true,
            ContinueConversation = true,
        };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--include-partial-messages");
        await Assert.That(args).Contains("--include-hook-events");
        await Assert.That(args).Contains("--strict-mcp-config");
        await Assert.That(args).Contains("--fork-session");
        await Assert.That(args).Contains("--continue");
    }

    // F7: --session-mirror is emitted whenever a SessionStore is configured (bijlage B row 26).
    [Test]
    public async Task Build_SessionStoreSet_EmitsSessionMirrorFlag()
    {
        var options = new ClaudeAgentOptions { SessionStore = new BD.Connectors.ClaudeCode.Sessions.InMemorySessionStore() };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(args).Contains("--session-mirror");
    }

    [Test]
    public async Task Build_NoSessionStore_OmitsSessionMirrorFlag()
    {
        var args = CliCommandBuilder.Build("claude", new ClaudeAgentOptions(), isWindows: false);

        await Assert.That(args).DoesNotContain("--session-mirror");
    }

    [Test]
    public async Task Build_PermissionMode_UsesWireValue()
    {
        var options = new ClaudeAgentOptions { PermissionMode = PermissionMode.BypassPermissions };

        var args = CliCommandBuilder.Build("claude", options, isWindows: false);

        await Assert.That(IndexAfter(args, "--permission-mode")).IsEqualTo("bypassPermissions");
    }

    [Test]
    [Arguments("Read", "Read")]
    [Arguments("Read(*)", "Read")]
    [Arguments("Bash(ls:*)", null)]
    public async Task ApplySkillsDefaults_ReturnsAllowedToolsUnchangedWhenNoSkillsConfigured(string entry, string? unusedExpectedWholeTool)
    {
        _ = unusedExpectedWholeTool;
        var options = new ClaudeAgentOptions { AllowedTools = [entry] };

        var (allowedTools, settingSources) = CliCommandBuilder.ApplySkillsDefaults(options);

        await Assert.That(allowedTools.Count).IsEqualTo(1);
        await Assert.That(allowedTools[0]).IsEqualTo(entry);
        await Assert.That(settingSources).IsNull();
    }

    [Test]
    [Arguments("skill-name", true)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("has(paren)", false)]
    [Arguments("has,comma", false)]
    [Arguments("*", false)]
    [Arguments("wild:*", false)]
    [Arguments("wild *", false)]
    [Arguments("/slash-form", false)]
    [Arguments(@"double\\backslash", false)]
    [Arguments(@"trailing\", false)]
    [Arguments(" leading-space", false)]
    [Arguments("trailing-space ", false)]
    public async Task ValidateSkillName_AcceptsOrRejectsPerCliRules(string name, bool expectedValid)
    {
        var action = () => CliCommandBuilder.ValidateSkillName(name);

        if (expectedValid)
        {
            action();
        }
        else
        {
            await Assert.That(action).Throws<ArgumentException>();
        }
    }

    private static string? IndexAfter(IReadOnlyList<string> args, string flag)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == flag)
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }
}
