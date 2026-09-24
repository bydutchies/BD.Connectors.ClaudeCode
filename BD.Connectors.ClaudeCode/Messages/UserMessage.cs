using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public sealed record UserMessage : Message
{
    /// <summary>Set when the CLI sent <c>message.content</c> as a plain string.</summary>
    public string? TextContent { get; init; }

    /// <summary>Set when the CLI sent <c>message.content</c> as a content-block array.</summary>
    public IReadOnlyList<ContentBlock>? BlockContent { get; init; }

    public string? Uuid { get; init; }

    public string? ParentToolUseId { get; init; }

    public JsonObject? ToolUseResult { get; init; }

    public JsonObject? Origin { get; init; }
}
