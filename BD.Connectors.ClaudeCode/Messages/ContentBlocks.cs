using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ThinkingBlock(string Thinking, string Signature) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonObject Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, JsonNode? Content = null, bool? IsError = null) : ContentBlock;

// Name stays a plain string (not an enum) so a newer CLI's server tool names never break parsing.
public sealed record ServerToolUseBlock(string Id, string Name, JsonObject Input) : ContentBlock;

public sealed record ServerToolResultBlock(string ToolUseId, JsonObject Content) : ContentBlock;
