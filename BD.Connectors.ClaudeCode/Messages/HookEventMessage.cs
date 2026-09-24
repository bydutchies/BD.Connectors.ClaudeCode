using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public sealed record HookEventMessage(
    string Subtype,
    JsonObject Data,
    string HookEventName = "",
    string? SessionId = null,
    string? Uuid = null) : SystemMessage(Subtype, Data);
