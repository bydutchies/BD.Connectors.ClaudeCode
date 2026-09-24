using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

// Key is JsonObject, not a typed SessionKey: that record is introduced by F7 (session stores).
public sealed record MirrorErrorMessage(
    string Subtype,
    JsonObject Data,
    JsonObject? Key = null,
    string Error = "") : SystemMessage(Subtype, Data);
