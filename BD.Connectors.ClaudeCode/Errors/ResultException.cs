using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Errors;

public sealed class ResultException : ProcessException
{
    // Named Payload, not PY's "data": Exception already declares a Data property of a different type.
    public JsonObject Payload { get; }

    public string? Subtype { get; }

    public IReadOnlyList<string> Errors { get; }

    public string? Result { get; }

    public int? ApiErrorStatus { get; }

    public string? TerminalReason { get; }

    public string? SessionId { get; }

    public ResultException(string message, JsonObject? data = null, int? exitCode = null, Exception? innerException = null)
        : base(message, exitCode, innerException: innerException)
    {
        Payload = data ?? [];
        Subtype = JsonHelpers.GetString(Payload, "subtype");
        Errors = NormalizeResultErrors(JsonHelpers.GetNode(Payload, "errors"));
        Result = JsonHelpers.GetString(Payload, "result");
        ApiErrorStatus = JsonHelpers.GetInt32(Payload, "api_error_status");
        TerminalReason = JsonHelpers.GetString(Payload, "terminal_reason");
        SessionId = JsonHelpers.GetString(Payload, "session_id");
    }

    internal static IReadOnlyList<string> NormalizeResultErrors(JsonNode? raw)
    {
        if (raw is JsonValue stringValue && stringValue.TryGetValue<string>(out var single))
        {
            return single.Trim().Length > 0 ? [single.Trim()] : [];
        }

        if (raw is not JsonArray array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var trimmed = text.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }
}
