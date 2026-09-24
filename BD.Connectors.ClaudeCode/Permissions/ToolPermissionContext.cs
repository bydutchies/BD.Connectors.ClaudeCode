using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Permissions;

public sealed record ToolPermissionContext
{
    public IReadOnlyList<PermissionUpdate> Suggestions { get; init; } = [];

    public string? ToolUseId { get; init; }

    public string? AgentId { get; init; }

    public string? BlockedPath { get; init; }

    public string? DecisionReason { get; init; }

    public string? Title { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    // Replaces PY's `signal`: cancelled on a control_cancel_request for this call, or on client close.
    public CancellationToken CancellationToken { get; init; }
}

public delegate Task<PermissionResult> CanUseToolCallback(string toolName, JsonObject input, ToolPermissionContext context);
