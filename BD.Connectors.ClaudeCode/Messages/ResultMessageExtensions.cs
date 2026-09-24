using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace BD.Connectors.ClaudeCode.Messages;

// GetStructuredOutput<T> -- typed access to ResultMessage.StructuredOutput. Not part of PY (PY only
// exposes the raw `structured_output: Any` field); this is an extra convenience
// ported from CS `ClaudeThread.DeserializeTypedResponse`/`DeserializeTypedResponseWithReflection`,
// adapted to deserialize from the already-parsed JsonNode this SDK exposes rather than CS's raw
// response string.
public static class ResultMessageExtensions
{
    private const string AotUnsafeMessage =
        "This overload relies on reflection-based JSON serialization and is not AOT/trimming-safe. Use the JsonTypeInfo<T> overload instead.";

    // AOT- and trimming-safe: deserializes via a source-generated JsonTypeInfo<T>.
    public static T GetStructuredOutput<T>(this ResultMessage message, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var node = RequireStructuredOutput<T>(message);
        try
        {
            return JsonSerializer.Deserialize(node, jsonTypeInfo) ?? throw EmptyStructuredOutputException<T>();
        }
        catch (JsonException e)
        {
            throw DeserializeFailedException<T>(e);
        }
    }

    // Convenience overload using reflection-based serialization; not AOT/trimming-safe. Prefer the
    // JsonTypeInfo<T> overload in trimmed or NativeAOT applications.
    [RequiresDynamicCode(AotUnsafeMessage)]
    [RequiresUnreferencedCode(AotUnsafeMessage)]
    public static T GetStructuredOutput<T>(this ResultMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var node = RequireStructuredOutput<T>(message);
        try
        {
            return JsonSerializer.Deserialize<T>(node) ?? throw EmptyStructuredOutputException<T>();
        }
        catch (JsonException e)
        {
            throw DeserializeFailedException<T>(e);
        }
    }

    private static JsonNode RequireStructuredOutput<T>(ResultMessage message) =>
        message.StructuredOutput ?? throw new InvalidOperationException($"ResultMessage has no structured output for type '{typeof(T).FullName}'.");

    private static InvalidOperationException EmptyStructuredOutputException<T>() =>
        new($"Model returned empty structured output for type '{typeof(T).FullName}'.");

    private static InvalidOperationException DeserializeFailedException<T>(Exception inner) =>
        new($"Failed to deserialize model response to type '{typeof(T).FullName}'.", inner);
}
