using BD.Connectors.ClaudeCode.Hooks;
using BD.Connectors.ClaudeCode.Logging;
using BD.Connectors.ClaudeCode.Options;
using Microsoft.Extensions.Logging;

namespace BD.Connectors.ClaudeCode.Internal;

internal static class OptionsConfigurator
{
    // Matches the CLI's rule parser: an entry allows a whole tool when it has no "(...)"
    // specifier, or the specifier is empty or a lone wildcard.
    public static string? WholeToolAllowed(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        var openIndex = entry.IndexOf('(');
        if (openIndex == -1)
        {
            return entry;
        }

        if (openIndex == 0 || !entry.EndsWith(')'))
        {
            return null;
        }

        var specifier = entry[(openIndex + 1)..^1];
        return specifier is "" or "*" ? entry[..openIndex] : null;
    }

    public static string? GetCanUseToolShadowedWarning(PermissionMode? permissionMode, IReadOnlyList<string> allowedTools)
    {
        if (permissionMode == PermissionMode.BypassPermissions)
        {
            return "can_use_tool will not be invoked: permission_mode 'bypassPermissions' auto-approves every "
                + "tool call (except explicit deny rules) before the callback is consulted. To gate every tool "
                + "call, use a PreToolUse hook instead.";
        }

        var shadowed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in allowedTools)
        {
            var tool = WholeToolAllowed(entry);
            if (tool is not null && seen.Add(tool))
            {
                shadowed.Add(tool);
            }
        }

        if (shadowed.Count == 0)
        {
            return null;
        }

        return $"can_use_tool will not be invoked for: {string.Join(", ", shadowed)}. An allowed_tools entry "
            + "that allows a whole tool auto-approves it before the callback is consulted. To gate every tool "
            + "call, use a PreToolUse hook; or narrow the entry so calls fall through to can_use_tool. Allow "
            + "rules from settings files can also shadow the callback but are not visible here.";
    }

    // Shared by QueryAsync and ClaudeSdkClient.ConnectAsync (F3/F4) so both enforce the same rules.
    public static ClaudeAgentOptions ConfigureCanUseTool(ClaudeAgentOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.CanUseTool is null)
        {
            return options;
        }

        if (!string.IsNullOrEmpty(options.PermissionPromptToolName))
        {
            throw new ArgumentException(
                "can_use_tool callback cannot be used with permission_prompt_tool_name. Please use one or the other.");
        }

        WarnIfCanUseToolShadowed(options, logger);

        return options with { PermissionPromptToolName = "stdio" };
    }

    // The two-field split (McpServers dict + McpConfig string) has no PY equivalent to validate
    // against: PY models both as a single `dict | str | Path` field, so this check exists only
    // because splitting it into two C# properties reopens a "set both" case PY's type shape ruled out.
    public static void EnsureMcpConfigNotAmbiguous(ClaudeAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.McpServers.Count > 0 && options.McpConfig is not null)
        {
            throw new ArgumentException("Cannot set both McpServers and McpConfig; use one or the other.");
        }
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<InternalHookMatcher>> HooksToInternal(
        IReadOnlyDictionary<HookEvent, IReadOnlyList<HookMatcher>> hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);

        var result = new Dictionary<string, IReadOnlyList<InternalHookMatcher>>();
        foreach (var (hookEvent, matchers) in hooks)
        {
            result[hookEvent.ToWireValue()] = matchers
                .Select(matcher => new InternalHookMatcher(matcher.Matcher, matcher.Hooks, matcher.TimeoutSeconds))
                .ToList();
        }

        return result;
    }

    private static void WarnIfCanUseToolShadowed(ClaudeAgentOptions options, ILogger? logger)
    {
        var allowedTools = options.AllowedTools;
        if (options.Skills is SkillsConfig.All && !allowedTools.Contains("Skill"))
        {
            allowedTools = [.. allowedTools, "Skill"];
        }

        var message = GetCanUseToolShadowedWarning(options.PermissionMode, allowedTools);
        if (message is not null && logger is not null)
        {
            SdkLog.CanUseToolShadowed(logger, message);
        }
    }
}

internal sealed record InternalHookMatcher(string? Matcher, IReadOnlyList<HookCallback> Callbacks, double? TimeoutSeconds);
