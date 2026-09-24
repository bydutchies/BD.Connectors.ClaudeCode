using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public sealed record DeferredToolUse(string Id, string Name, JsonObject Input);

public sealed record ResultMessage(
    string Subtype,
    long DurationMs,
    long DurationApiMs,
    bool IsError,
    int NumTurns,
    string SessionId) : Message
{
    public string? StopReason { get; init; }

    public double? TotalCostUsd { get; init; }

    public JsonObject? Usage { get; init; }

    public string? Result { get; init; }

    public JsonNode? StructuredOutput { get; init; }

    public JsonObject? ModelUsage { get; init; }

    public JsonArray? PermissionDenials { get; init; }

    public DeferredToolUse? DeferredToolUse { get; init; }

    public IReadOnlyList<string>? Errors { get; init; }

    public int? ApiErrorStatus { get; init; }

    public string? Uuid { get; init; }

    public string? TerminalReason { get; init; }

    public JsonObject? Origin { get; init; }
}
