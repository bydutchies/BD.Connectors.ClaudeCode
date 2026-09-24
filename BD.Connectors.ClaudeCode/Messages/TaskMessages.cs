using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public sealed record TaskUsage(long TotalTokens, long ToolUses, long DurationMs);

public sealed record TaskStartedMessage(
    string Subtype,
    JsonObject Data,
    string TaskId,
    string Description,
    string Uuid,
    string SessionId,
    string? ToolUseId = null,
    string? TaskType = null) : SystemMessage(Subtype, Data);

public sealed record TaskProgressMessage(
    string Subtype,
    JsonObject Data,
    string TaskId,
    string Description,
    TaskUsage Usage,
    string Uuid,
    string SessionId,
    string? ToolUseId = null,
    string? LastToolName = null) : SystemMessage(Subtype, Data);

public sealed record TaskNotificationMessage(
    string Subtype,
    JsonObject Data,
    string TaskId,
    string Status,
    string OutputFile,
    string Summary,
    string Uuid,
    string SessionId,
    string? ToolUseId = null,
    TaskUsage? Usage = null) : SystemMessage(Subtype, Data);

public sealed record TaskUpdatedMessage(
    string Subtype,
    JsonObject Data,
    string TaskId,
    JsonObject Patch,
    string? Status = null,
    string? SessionId = null,
    string? Uuid = null) : SystemMessage(Subtype, Data);
