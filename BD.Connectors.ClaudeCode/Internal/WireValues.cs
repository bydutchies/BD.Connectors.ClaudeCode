using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Internal;

internal static class WireValues
{
    public static string ToWireValue(this PermissionMode value) => value switch
    {
        PermissionMode.Default => "default",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        PermissionMode.DontAsk => "dontAsk",
        PermissionMode.Auto => "auto",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static PermissionMode? PermissionModeFromWireValue(string? value) => value switch
    {
        "default" => PermissionMode.Default,
        "acceptEdits" => PermissionMode.AcceptEdits,
        "plan" => PermissionMode.Plan,
        "bypassPermissions" => PermissionMode.BypassPermissions,
        "dontAsk" => PermissionMode.DontAsk,
        "auto" => PermissionMode.Auto,
        _ => null,
    };

    public static string ToWireValue(this SettingSource value) => value switch
    {
        SettingSource.User => "user",
        SettingSource.Project => "project",
        SettingSource.Local => "local",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToWireValue(this EffortLevel value) => value switch
    {
        EffortLevel.Low => "low",
        EffortLevel.Medium => "medium",
        EffortLevel.High => "high",
        EffortLevel.XHigh => "xhigh",
        EffortLevel.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToWireValue(this HookEvent value) => value.ToString();

    public static HookEvent? HookEventFromWireValue(string? value) => Enum.TryParse<HookEvent>(value, out var result) ? result : null;

    public static string ToWireValue(this PermissionBehavior value) => value switch
    {
        PermissionBehavior.Allow => "allow",
        PermissionBehavior.Deny => "deny",
        PermissionBehavior.Ask => "ask",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static PermissionBehavior? PermissionBehaviorFromWireValue(string? value) => value switch
    {
        "allow" => PermissionBehavior.Allow,
        "deny" => PermissionBehavior.Deny,
        "ask" => PermissionBehavior.Ask,
        _ => null,
    };

    public static string ToWireValue(this PermissionUpdateType value) => value switch
    {
        PermissionUpdateType.AddRules => "addRules",
        PermissionUpdateType.ReplaceRules => "replaceRules",
        PermissionUpdateType.RemoveRules => "removeRules",
        PermissionUpdateType.SetMode => "setMode",
        PermissionUpdateType.AddDirectories => "addDirectories",
        PermissionUpdateType.RemoveDirectories => "removeDirectories",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static PermissionUpdateType? PermissionUpdateTypeFromWireValue(string? value) => value switch
    {
        "addRules" => PermissionUpdateType.AddRules,
        "replaceRules" => PermissionUpdateType.ReplaceRules,
        "removeRules" => PermissionUpdateType.RemoveRules,
        "setMode" => PermissionUpdateType.SetMode,
        "addDirectories" => PermissionUpdateType.AddDirectories,
        "removeDirectories" => PermissionUpdateType.RemoveDirectories,
        _ => null,
    };

    public static string ToWireValue(this PermissionUpdateDestination value) => value switch
    {
        PermissionUpdateDestination.UserSettings => "userSettings",
        PermissionUpdateDestination.ProjectSettings => "projectSettings",
        PermissionUpdateDestination.LocalSettings => "localSettings",
        PermissionUpdateDestination.Session => "session",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static PermissionUpdateDestination? PermissionUpdateDestinationFromWireValue(string? value) => value switch
    {
        "userSettings" => PermissionUpdateDestination.UserSettings,
        "projectSettings" => PermissionUpdateDestination.ProjectSettings,
        "localSettings" => PermissionUpdateDestination.LocalSettings,
        "session" => PermissionUpdateDestination.Session,
        _ => null,
    };

    public static string ToWireValue(this ThinkingDisplay value) => value switch
    {
        ThinkingDisplay.Summarized => "summarized",
        ThinkingDisplay.Omitted => "omitted",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToWireValue(this SessionStoreFlushMode value) => value switch
    {
        SessionStoreFlushMode.Batched => "batched",
        SessionStoreFlushMode.Eager => "eager",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToWireValue(this AgentMemory value) => value switch
    {
        AgentMemory.User => "user",
        AgentMemory.Project => "project",
        AgentMemory.Local => "local",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
