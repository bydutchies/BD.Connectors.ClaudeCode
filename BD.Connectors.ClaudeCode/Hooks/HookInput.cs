using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Hooks;

public abstract record HookInput(JsonObject Raw)
{
    public string SessionId { get; init; } = string.Empty;

    public string TranscriptPath { get; init; } = string.Empty;

    public string Cwd { get; init; } = string.Empty;

    public PermissionMode? PermissionMode { get; init; }

    public string HookEventName { get; init; } = string.Empty;

    public static HookInput FromJson(JsonObject data)
    {
        var hookEventName = JsonHelpers.GetString(data, "hook_event_name") ?? string.Empty;

        HookInput result = hookEventName switch
        {
            "PreToolUse" => new PreToolUseHookInput(data)
            {
                ToolName = JsonHelpers.GetString(data, "tool_name") ?? string.Empty,
                ToolInput = JsonHelpers.GetObject(data, "tool_input") ?? [],
                ToolUseId = JsonHelpers.GetString(data, "tool_use_id") ?? string.Empty,
                AgentId = JsonHelpers.GetString(data, "agent_id"),
                AgentType = JsonHelpers.GetString(data, "agent_type"),
            },
            "PostToolUse" => new PostToolUseHookInput(data)
            {
                ToolName = JsonHelpers.GetString(data, "tool_name") ?? string.Empty,
                ToolInput = JsonHelpers.GetObject(data, "tool_input") ?? [],
                ToolResponse = JsonHelpers.GetNode(data, "tool_response"),
                ToolUseId = JsonHelpers.GetString(data, "tool_use_id") ?? string.Empty,
                AgentId = JsonHelpers.GetString(data, "agent_id"),
                AgentType = JsonHelpers.GetString(data, "agent_type"),
            },
            "PostToolUseFailure" => new PostToolUseFailureHookInput(data)
            {
                ToolName = JsonHelpers.GetString(data, "tool_name") ?? string.Empty,
                ToolInput = JsonHelpers.GetObject(data, "tool_input") ?? [],
                ToolUseId = JsonHelpers.GetString(data, "tool_use_id") ?? string.Empty,
                Error = JsonHelpers.GetString(data, "error") ?? string.Empty,
                IsInterrupt = JsonHelpers.GetBool(data, "is_interrupt"),
                AgentId = JsonHelpers.GetString(data, "agent_id"),
                AgentType = JsonHelpers.GetString(data, "agent_type"),
            },
            "UserPromptSubmit" => new UserPromptSubmitHookInput(data)
            {
                Prompt = JsonHelpers.GetString(data, "prompt") ?? string.Empty,
            },
            "Stop" => new StopHookInput(data)
            {
                StopHookActive = JsonHelpers.GetBool(data, "stop_hook_active") ?? false,
            },
            "SubagentStop" => new SubagentStopHookInput(data)
            {
                StopHookActive = JsonHelpers.GetBool(data, "stop_hook_active") ?? false,
                AgentId = JsonHelpers.GetString(data, "agent_id") ?? string.Empty,
                AgentTranscriptPath = JsonHelpers.GetString(data, "agent_transcript_path") ?? string.Empty,
                AgentType = JsonHelpers.GetString(data, "agent_type") ?? string.Empty,
            },
            "PreCompact" => new PreCompactHookInput(data)
            {
                Trigger = JsonHelpers.GetString(data, "trigger") ?? string.Empty,
                CustomInstructions = JsonHelpers.GetString(data, "custom_instructions"),
            },
            "Notification" => new NotificationHookInput(data)
            {
                Message = JsonHelpers.GetString(data, "message") ?? string.Empty,
                Title = JsonHelpers.GetString(data, "title"),
                NotificationType = JsonHelpers.GetString(data, "notification_type") ?? string.Empty,
            },
            "SubagentStart" => new SubagentStartHookInput(data)
            {
                AgentId = JsonHelpers.GetString(data, "agent_id") ?? string.Empty,
                AgentType = JsonHelpers.GetString(data, "agent_type") ?? string.Empty,
            },
            "PermissionRequest" => new PermissionRequestHookInput(data)
            {
                ToolName = JsonHelpers.GetString(data, "tool_name") ?? string.Empty,
                ToolInput = JsonHelpers.GetObject(data, "tool_input") ?? [],
                PermissionSuggestions = JsonHelpers.GetArray(data, "permission_suggestions"),
                AgentId = JsonHelpers.GetString(data, "agent_id"),
                AgentType = JsonHelpers.GetString(data, "agent_type"),
            },
            _ => new UnknownHookInput(data),
        };

        return result with
        {
            SessionId = JsonHelpers.GetString(data, "session_id") ?? string.Empty,
            TranscriptPath = JsonHelpers.GetString(data, "transcript_path") ?? string.Empty,
            Cwd = JsonHelpers.GetString(data, "cwd") ?? string.Empty,
            PermissionMode = WireValues.PermissionModeFromWireValue(JsonHelpers.GetString(data, "permission_mode")),
            HookEventName = hookEventName,
        };
    }
}

public sealed record PreToolUseHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string ToolName { get; init; } = string.Empty;

    public JsonObject ToolInput { get; init; } = [];

    public string ToolUseId { get; init; } = string.Empty;

    public string? AgentId { get; init; }

    public string? AgentType { get; init; }
}

public sealed record PostToolUseHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string ToolName { get; init; } = string.Empty;

    public JsonObject ToolInput { get; init; } = [];

    public JsonNode? ToolResponse { get; init; }

    public string ToolUseId { get; init; } = string.Empty;

    public string? AgentId { get; init; }

    public string? AgentType { get; init; }
}

public sealed record PostToolUseFailureHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string ToolName { get; init; } = string.Empty;

    public JsonObject ToolInput { get; init; } = [];

    public string ToolUseId { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public bool? IsInterrupt { get; init; }

    public string? AgentId { get; init; }

    public string? AgentType { get; init; }
}

public sealed record UserPromptSubmitHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string Prompt { get; init; } = string.Empty;
}

public sealed record StopHookInput(JsonObject Raw) : HookInput(Raw)
{
    public bool StopHookActive { get; init; }
}

public sealed record SubagentStopHookInput(JsonObject Raw) : HookInput(Raw)
{
    public bool StopHookActive { get; init; }

    public string AgentId { get; init; } = string.Empty;

    public string AgentTranscriptPath { get; init; } = string.Empty;

    public string AgentType { get; init; } = string.Empty;
}

public sealed record PreCompactHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string Trigger { get; init; } = string.Empty;

    public string? CustomInstructions { get; init; }
}

public sealed record NotificationHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string Message { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string NotificationType { get; init; } = string.Empty;
}

public sealed record SubagentStartHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string AgentId { get; init; } = string.Empty;

    public string AgentType { get; init; } = string.Empty;
}

public sealed record PermissionRequestHookInput(JsonObject Raw) : HookInput(Raw)
{
    public string ToolName { get; init; } = string.Empty;

    public JsonObject ToolInput { get; init; } = [];

    public JsonArray? PermissionSuggestions { get; init; }

    public string? AgentId { get; init; }

    public string? AgentType { get; init; }
}

// Forward-compatible fallback for hook events this SDK version doesn't model yet.
public sealed record UnknownHookInput(JsonObject Raw) : HookInput(Raw);
