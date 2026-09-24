using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Logging;
using BD.Connectors.ClaudeCode.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Transport;

// Builds the CLI argument list, ported from PY. Returns arguments WITHOUT the executable path (that
// goes into ProcessStartInfo.FileName).
internal static partial class CliCommandBuilder
{
    // Parentheses and commas are delimiters to the --allowedTools tokenizer; control characters
    // (C0, DEL, C1) never appear in a skill directory name. U+FEFF is here because the CLI trims it
    // as whitespace and .NET's Trim() does not.
    [GeneratedRegex(@"[(),\x00-\x1f\x7f-\x9f﻿]")]
    private static partial Regex SkillNameInvalidChars();

    public static IReadOnlyList<string> Build(string cliPath, ClaudeAgentOptions options, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = cliPath; // not included in the returned argument list; kept for signature parity with the plan.

        var logger = options.Logger ?? NullLogger.Instance;
        var cmd = new List<string>
        {
            Flag(WireConstants.CliFlags.OutputFormat), "stream-json",
            Flag(WireConstants.CliFlags.Verbose),
        };

        AddSystemPrompt(cmd, options);
        AddTools(cmd, options);

        var (effectiveAllowedTools, effectiveSettingSources) = ApplySkillsDefaults(options);
        if (effectiveAllowedTools.Count > 0)
        {
            cmd.Add(Flag(WireConstants.CliFlags.AllowedTools));
            cmd.Add(string.Join(',', effectiveAllowedTools));
        }

        if (options.MaxTurns is { } maxTurns && maxTurns != 0)
        {
            cmd.Add(Flag(WireConstants.CliFlags.MaxTurns));
            cmd.Add(maxTurns.ToString(CultureInfo.InvariantCulture));
        }

        if (options.MaxBudgetUsd is { } maxBudgetUsd)
        {
            cmd.Add(Flag(WireConstants.CliFlags.MaxBudgetUsd));
            cmd.Add(maxBudgetUsd.ToString(CultureInfo.InvariantCulture));
        }

        if (options.DisallowedTools.Count > 0)
        {
            cmd.Add(Flag(WireConstants.CliFlags.DisallowedTools));
            cmd.Add(string.Join(',', options.DisallowedTools));
        }

        if (options.TaskBudget is { } taskBudget)
        {
            cmd.Add(Flag(WireConstants.CliFlags.TaskBudget));
            cmd.Add(taskBudget.Total.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(options.Model))
        {
            cmd.Add(Flag(WireConstants.CliFlags.Model));
            cmd.Add(options.Model);
        }

        if (!string.IsNullOrEmpty(options.FallbackModel))
        {
            cmd.Add(Flag(WireConstants.CliFlags.FallbackModel));
            cmd.Add(options.FallbackModel);
        }

        if (options.Betas.Count > 0)
        {
            cmd.Add(Flag(WireConstants.CliFlags.Betas));
            cmd.Add(string.Join(',', options.Betas));
        }

        if (!string.IsNullOrEmpty(options.PermissionPromptToolName))
        {
            cmd.Add(Flag(WireConstants.CliFlags.PermissionPromptTool));
            cmd.Add(options.PermissionPromptToolName);
        }

        if (options.PermissionMode is { } permissionMode)
        {
            cmd.Add(Flag(WireConstants.CliFlags.PermissionMode));
            cmd.Add(permissionMode.ToWireValue());
        }

        if (options.ContinueConversation)
        {
            cmd.Add(Flag(WireConstants.CliFlags.Continue));
        }

        // Passed as --flag=value rather than as two argv tokens: the CLI declares --resume with an
        // optional value, so in the two-token form a dash-leading value would not bind to the flag
        // and would instead parse as a separate CLI flag, letting an untrusted value inject arbitrary
        // flags. The equals form always binds the value to the flag.
        if (!string.IsNullOrEmpty(options.Resume))
        {
            CliLocator.RejectWindowsCmdMetacharacters("resume", options.Resume, isWindows);
            cmd.Add($"--{WireConstants.CliFlags.Resume}={options.Resume}");
        }

        if (!string.IsNullOrEmpty(options.SessionId))
        {
            CliLocator.RejectWindowsCmdMetacharacters("session_id", options.SessionId, isWindows);
            cmd.Add($"--{WireConstants.CliFlags.SessionId}={options.SessionId}");
        }

        var settingsValue = BuildSettingsValue(options, logger);
        if (!string.IsNullOrEmpty(settingsValue))
        {
            cmd.Add(Flag(WireConstants.CliFlags.Settings));
            cmd.Add(settingsValue);
        }

        foreach (var dir in options.AddDirs)
        {
            cmd.Add(Flag(WireConstants.CliFlags.AddDir));
            cmd.Add(dir);
        }

        AddMcpConfig(cmd, options);

        if (options.IncludePartialMessages)
        {
            cmd.Add(Flag(WireConstants.CliFlags.IncludePartialMessages));
        }

        if (options.IncludeHookEvents)
        {
            cmd.Add(Flag(WireConstants.CliFlags.IncludeHookEvents));
        }

        if (options.StrictMcpConfig)
        {
            cmd.Add(Flag(WireConstants.CliFlags.StrictMcpConfig));
        }

        if (options.ForkSession)
        {
            cmd.Add(Flag(WireConstants.CliFlags.ForkSession));
        }

        if (!string.IsNullOrEmpty(options.ResumeSessionAt))
        {
            CliLocator.RejectWindowsCmdMetacharacters("resume_session_at", options.ResumeSessionAt, isWindows);
            cmd.Add($"--{WireConstants.CliFlags.ResumeSessionAt}={options.ResumeSessionAt}");
        }

        // `is not null`, not truthiness: an empty string is forwarded so the CLI rejects it as a
        // malformed declaration instead of the SDK silently disarming the guard the caller believes
        // is armed.
        if (options.ResumeDropsTurn is not null)
        {
            CliLocator.RejectWindowsCmdMetacharacters("resume_drops_turn", options.ResumeDropsTurn, isWindows);
            cmd.Add($"--{WireConstants.CliFlags.ResumeDropsTurn}={options.ResumeDropsTurn}");
        }

        if (options.SessionStore is not null)
        {
            cmd.Add(Flag(WireConstants.CliFlags.SessionMirror));
        }

        // Agents are always sent via the initialize control_request (F3), matching the TypeScript
        // SDK; there is no --agents CLI flag.
        if (effectiveSettingSources is not null)
        {
            cmd.Add($"--{WireConstants.CliFlags.SettingSources}={string.Join(',', effectiveSettingSources.Select(s => s.ToWireValue()))}");
        }

        foreach (var plugin in options.Plugins)
        {
            if (plugin.Type != "local")
            {
                throw new ArgumentException($"Unsupported plugin type: {plugin.Type}");
            }

            cmd.Add(Flag(WireConstants.CliFlags.PluginDir));
            cmd.Add(plugin.Path);
        }

        foreach (var (flag, value) in options.ExtraArgs)
        {
            if (value is null)
            {
                cmd.Add($"--{flag}");
            }
            else if (value.StartsWith('-'))
            {
                // In the two-token form, a dash-leading value is not bound to its flag when the CLI
                // declares the option with an optional value; the equals form always binds.
                cmd.Add($"--{flag}={value}");
            }
            else
            {
                cmd.Add($"--{flag}");
                cmd.Add(value);
            }
        }

        AddThinking(cmd, options);

        if (options.Effort is { } effort)
        {
            cmd.Add(Flag(WireConstants.CliFlags.Effort));
            cmd.Add(effort.ToWireValue());
        }

        if (options.OutputFormat is { } outputFormat
            && JsonHelpers.GetString(outputFormat, "type") == "json_schema"
            && JsonHelpers.GetNode(outputFormat, "schema") is { } schema)
        {
            cmd.Add(Flag(WireConstants.CliFlags.JsonSchema));
            cmd.Add(schema.ToJsonString());
        }

        // Always streaming mode with stdin: allows agents and other large configs to be sent via the
        // initialize request.
        cmd.Add(Flag(WireConstants.CliFlags.InputFormat));
        cmd.Add("stream-json");

        return cmd;
    }

    // Computes effective AllowedTools/SettingSources for skills. Skills.All injects the bare "Skill"
    // tool; Skills.Named injects "Skill(name)" for each entry. In either case SettingSources defaults
    // to [User, Project] when unset, so the CLI discovers installed skills without the caller having
    // to wire up both options manually. Does not mutate the original options object.
    internal static (IReadOnlyList<string> AllowedTools, IReadOnlyList<SettingSource>? SettingSources) ApplySkillsDefaults(ClaudeAgentOptions options)
    {
        var allowedTools = new List<string>(options.AllowedTools);
        var settingSources = options.SettingSources;

        switch (options.Skills)
        {
            case null:
                return (allowedTools, settingSources);
            case SkillsConfig.All:
                if (!allowedTools.Contains("Skill"))
                {
                    allowedTools.Add("Skill");
                }

                break;
            case SkillsConfig.Named named:
                foreach (var name in named.Skills)
                {
                    ValidateSkillName(name);
                    var pattern = $"Skill({name})";
                    if (!allowedTools.Contains(pattern))
                    {
                        allowedTools.Add(pattern);
                    }
                }

                break;
        }

        settingSources ??= [SettingSource.User, SettingSource.Project];
        return (allowedTools, settingSources);
    }

    // Rejects skill names that cannot ride safely in a "Skill(name)" rule. Names from
    // options.Skills are formatted into the --allowedTools value, which the CLI splits into rules on
    // commas and spaces outside parentheses; that tokenizer does not honor escape sequences, so a
    // name carrying a delimiter cannot be passed through reliably.
    internal static void ValidateSkillName(string name)
    {
        if (name.Trim().Length == 0)
        {
            throw new ArgumentException("Skill names must be non-empty strings");
        }

        if (ContainsLoneSurrogate(name))
        {
            throw new ArgumentException(
                $"Invalid skill name {CliLocator.PyRepr(name)}: contains a surrogate code point, which can "
                + "never match a skill the CLI discovered.");
        }

        if (name != name.Trim())
        {
            throw new ArgumentException(
                $"Invalid skill name {CliLocator.PyRepr(name)}: leading or trailing whitespace can never "
                + "match — the Skill tool trims the invoked name.");
        }

        if (SkillNameInvalidChars().IsMatch(name))
        {
            throw new ArgumentException(
                $"Invalid skill name {CliLocator.PyRepr(name)}: parentheses, commas, control characters, and "
                + "byte-order marks are not allowed. Names match the skill's directory name, or "
                + "'plugin:skill' for plugin-qualified skills.");
        }

        if (name == "*")
        {
            throw new ArgumentException("Invalid skill name '*': use skills=\"all\" to enable every skill.");
        }

        if (name.EndsWith(":*", StringComparison.Ordinal) || name.EndsWith(" *", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Invalid skill name {CliLocator.PyRepr(name)}: wildcard-suffix names are not allowed; list "
                + "each skill by its exact name.");
        }

        if (name.StartsWith('/'))
        {
            throw new ArgumentException(
                $"Invalid skill name {CliLocator.PyRepr(name)}: skill names may not start with '/'. The "
                + "skills option takes the canonical name, not the slash-command form.");
        }

        if (name.Contains("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Invalid skill name {CliLocator.PyRepr(name)}: consecutive backslashes are not allowed — "
                + "the per-rule parser collapses them, so the rule would name a different skill.");
        }

        if (name.EndsWith('\\'))
        {
            throw new ArgumentException($"Invalid skill name {CliLocator.PyRepr(name)}: names may not end with an unpaired backslash.");
        }
    }

    // Builds the --settings value, merging sandbox settings if provided.
    //
    //   - Neither settings nor sandbox -> null.
    //   - Only settings, no sandbox -> passed through as-is (JSON string or file path).
    //   - Sandbox present -> settings (parsed from JSON or read from a file) merged with "sandbox",
    //     re-serialized.
    internal static string? BuildSettingsValue(ClaudeAgentOptions options, ILogger logger)
    {
        var hasSettings = options.Settings is not null;
        var hasSandbox = options.Sandbox is not null;

        if (!hasSettings && !hasSandbox)
        {
            return null;
        }

        if (hasSettings && !hasSandbox)
        {
            return options.Settings;
        }

        var settingsObj = new JsonObject();
        if (hasSettings)
        {
            var settingsStr = options.Settings!.Trim();
            if (settingsStr.StartsWith('{') && settingsStr.EndsWith('}'))
            {
                try
                {
                    settingsObj = JsonNode.Parse(settingsStr)?.AsObject() ?? new JsonObject();
                }
                catch (JsonException)
                {
                    SdkLog.SettingsParseFailedTreatingAsFilePath(logger, settingsStr);
                    settingsObj = ReadSettingsFile(settingsStr, logger);
                }
            }
            else
            {
                settingsObj = ReadSettingsFile(settingsStr, logger);
            }
        }

        if (hasSandbox)
        {
            settingsObj["sandbox"] = options.Sandbox!.ToJson();
        }

        return settingsObj.ToJsonString();
    }

    private static JsonObject ReadSettingsFile(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            SdkLog.SettingsFileNotFound(logger, path);
            return [];
        }

        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)?.AsObject() ?? [];
    }

    // Every surrogate char that is part of a well-formed .NET string is one half of a valid pair
    // (.NET strings are UTF-16, unlike Python's codepoint strings, so a valid astral character, e.g.
    // an emoji, legitimately contains two surrogate code units). Only a genuinely unpaired surrogate
    // -- which can never match a skill directory name -- is rejected.
    private static bool ContainsLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddSystemPrompt(List<string> cmd, ClaudeAgentOptions options)
    {
        switch (options.SystemPrompt)
        {
            case null:
                cmd.Add(Flag(WireConstants.CliFlags.SystemPrompt));
                cmd.Add(string.Empty);
                break;
            case SystemPromptConfig.Text text:
                cmd.Add(Flag(WireConstants.CliFlags.SystemPrompt));
                cmd.Add(text.Value);
                break;
            case SystemPromptConfig.File file:
                cmd.Add(Flag(WireConstants.CliFlags.SystemPromptFile));
                cmd.Add(file.Path);
                break;
            case SystemPromptConfig.Custom custom:
                cmd.Add(Flag(WireConstants.CliFlags.SystemPrompt));
                cmd.Add(custom.Prompt);
                break;
            case SystemPromptConfig.Preset { Append: { } append }:
                cmd.Add(Flag(WireConstants.CliFlags.AppendSystemPrompt));
                cmd.Add(append);
                break;
            case SystemPromptConfig.Preset:
                break;
        }
    }

    private static void AddTools(List<string> cmd, ClaudeAgentOptions options)
    {
        switch (options.Tools)
        {
            case null:
                return;
            case ToolsConfig.Named { Tools.Count: 0 }:
                cmd.Add(Flag(WireConstants.CliFlags.Tools));
                cmd.Add(string.Empty);
                break;
            case ToolsConfig.Named named:
                cmd.Add(Flag(WireConstants.CliFlags.Tools));
                cmd.Add(string.Join(',', named.Tools));
                break;
            case ToolsConfig.ClaudeCodePreset:
                cmd.Add(Flag(WireConstants.CliFlags.Tools));
                cmd.Add("default");
                break;
        }
    }

    private static void AddMcpConfig(List<string> cmd, ClaudeAgentOptions options)
    {
        if (options.McpServers.Count > 0)
        {
            var serversForCli = new JsonObject();
            foreach (var (name, config) in options.McpServers)
            {
                serversForCli[name] = config.ToJson();
            }

            cmd.Add(Flag(WireConstants.CliFlags.McpConfig));
            cmd.Add(new JsonObject { ["mcpServers"] = serversForCli }.ToJsonString());
        }
        else if (options.McpConfig is not null)
        {
            cmd.Add(Flag(WireConstants.CliFlags.McpConfig));
            cmd.Add(options.McpConfig);
        }
    }

    private static void AddThinking(List<string> cmd, ClaudeAgentOptions options)
    {
        // `thinking` takes precedence over the deprecated `max_thinking_tokens`.
        switch (options.Thinking)
        {
            case null:
                if (options.MaxThinkingTokens is { } maxThinkingTokens)
                {
                    cmd.Add(Flag(WireConstants.CliFlags.MaxThinkingTokens));
                    cmd.Add(maxThinkingTokens.ToString(CultureInfo.InvariantCulture));
                }

                return;
            case ThinkingConfig.Adaptive adaptive:
                cmd.Add(Flag(WireConstants.CliFlags.Thinking));
                cmd.Add("adaptive");
                AddThinkingDisplay(cmd, adaptive.Display);
                return;
            case ThinkingConfig.Enabled enabled:
                cmd.Add(Flag(WireConstants.CliFlags.MaxThinkingTokens));
                cmd.Add(enabled.BudgetTokens.ToString(CultureInfo.InvariantCulture));
                AddThinkingDisplay(cmd, enabled.Display);
                return;
            case ThinkingConfig.Disabled:
                cmd.Add(Flag(WireConstants.CliFlags.Thinking));
                cmd.Add("disabled");
                return;
        }
    }

    private static void AddThinkingDisplay(List<string> cmd, ThinkingDisplay? display)
    {
        if (display is { } value)
        {
            cmd.Add(Flag(WireConstants.CliFlags.ThinkingDisplay));
            cmd.Add(value.ToWireValue());
        }
    }

    private static string Flag(string name) => $"--{name}";
}
