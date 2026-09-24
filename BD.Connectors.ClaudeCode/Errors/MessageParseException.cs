using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Errors;

public sealed class MessageParseException(string message, JsonObject? data = null) : ClaudeSdkException(message)
{
    // Named Payload, not PY's "data": Exception already declares a Data property of a different type.
    public JsonObject? Payload { get; } = data;
}
