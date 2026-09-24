namespace BD.Connectors.ClaudeCode.Options;

public enum PermissionMode
{
    Default,
    AcceptEdits,
    Plan,
    BypassPermissions,
    DontAsk,
    Auto,
}

public enum SettingSource
{
    User,
    Project,
    Local,
}

public enum EffortLevel
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

public enum HookEvent
{
    PreToolUse,
    PostToolUse,
    PostToolUseFailure,
    UserPromptSubmit,
    Stop,
    SubagentStop,
    PreCompact,
    Notification,
    SubagentStart,
    PermissionRequest,
}

public enum PermissionBehavior
{
    Allow,
    Deny,
    Ask,
}

public enum PermissionUpdateType
{
    AddRules,
    ReplaceRules,
    RemoveRules,
    SetMode,
    AddDirectories,
    RemoveDirectories,
}

public enum PermissionUpdateDestination
{
    UserSettings,
    ProjectSettings,
    LocalSettings,
    Session,
}

public enum ThinkingDisplay
{
    Summarized,
    Omitted,
}

public enum SessionStoreFlushMode
{
    Batched,
    Eager,
}

public enum AgentMemory
{
    User,
    Project,
    Local,
}
