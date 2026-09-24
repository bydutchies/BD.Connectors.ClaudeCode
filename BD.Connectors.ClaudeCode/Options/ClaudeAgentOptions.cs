using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Hooks;
using BD.Connectors.ClaudeCode.Permissions;
using BD.Connectors.ClaudeCode.Sessions;
using Microsoft.Extensions.Logging;

namespace BD.Connectors.ClaudeCode.Options;

// PY's `user` (spawn the CLI process as another OS user) has no .NET equivalent on Unix and is
// intentionally not ported; see PORTING_STATUS.md "Afwijkingen van PY".
public sealed record ClaudeAgentOptions
{
    public ToolsConfig? Tools { get; init; }

    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    // null means an empty system prompt (--system-prompt ""), matching PY.
    public SystemPromptConfig? SystemPrompt { get; init; }

    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; } = new Dictionary<string, McpServerConfig>();

    // Mutually exclusive with McpServers (PY models both as one `str | Path | dict` field);
    // enforced by OptionsConfigurator.EnsureMcpConfigNotAmbiguous.
    public string? McpConfig { get; init; }

    public bool StrictMcpConfig { get; init; }

    public PermissionMode? PermissionMode { get; init; }

    public bool ContinueConversation { get; init; }

    public string? Resume { get; init; }

    public string? SessionId { get; init; }

    public int? MaxTurns { get; init; }

    public double? MaxBudgetUsd { get; init; }

    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    public string? Model { get; init; }

    public string? FallbackModel { get; init; }

    public IReadOnlyList<string> Betas { get; init; } = [];

    public string? PermissionPromptToolName { get; init; }

    public string? Cwd { get; init; }

    public string? CliPath { get; init; }

    public string? Settings { get; init; }

    public IReadOnlyList<string> AddDirs { get; init; } = [];

    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string?> ExtraArgs { get; init; } = new Dictionary<string, string?>();

    public int? MaxBufferSize { get; init; }

    public Action<string>? Stderr { get; init; }

    public CanUseToolCallback? CanUseTool { get; init; }

    public IReadOnlyDictionary<HookEvent, IReadOnlyList<HookMatcher>>? Hooks { get; init; }

    public bool IncludePartialMessages { get; init; }

    public bool IncludeHookEvents { get; init; }

    public bool ForwardSubagentText { get; init; }

    public bool VerbatimPrompts { get; init; }

    public bool ForkSession { get; init; }

    public string? ResumeSessionAt { get; init; }

    public string? ResumeDropsTurn { get; init; }

    public IReadOnlyDictionary<string, AgentDefinition>? Agents { get; init; }

    // null means "load every source" (CLI default); [] disables filesystem settings entirely.
    public IReadOnlyList<SettingSource>? SettingSources { get; init; }

    public SkillsConfig? Skills { get; init; }

    public SandboxSettings? Sandbox { get; init; }

    public IReadOnlyList<SdkPluginConfig> Plugins { get; init; } = [];

    public int? MaxThinkingTokens { get; init; }

    public ThinkingConfig? Thinking { get; init; }

    public EffortLevel? Effort { get; init; }

    public JsonObject? OutputFormat { get; init; }

    public bool EnableFileCheckpointing { get; init; }

    public ISessionStore? SessionStore { get; init; }

    public SessionStoreFlushMode SessionStoreFlush { get; init; } = SessionStoreFlushMode.Batched;

    public int LoadTimeoutMs { get; init; } = 60_000;

    public TaskBudget? TaskBudget { get; init; }

    public ILogger? Logger { get; init; }
}
