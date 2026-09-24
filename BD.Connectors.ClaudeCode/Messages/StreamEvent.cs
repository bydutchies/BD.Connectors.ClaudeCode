using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public sealed record StreamEvent(string Uuid, string SessionId, JsonObject Event, string? ParentToolUseId = null) : Message;
