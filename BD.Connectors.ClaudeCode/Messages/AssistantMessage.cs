using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public sealed record AssistantMessage(IReadOnlyList<ContentBlock> Content, string Model) : Message
{
    public string? ParentToolUseId { get; init; }

    public string? Error { get; init; }

    public JsonObject? Usage { get; init; }

    public string? MessageId { get; init; }

    public string? StopReason { get; init; }

    public string? SessionId { get; init; }

    public string? Uuid { get; init; }
}
