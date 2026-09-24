using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace BD.Connectors.ClaudeCode.Options;

public static class OutputFormats
{
    public static JsonObject JsonSchema(JsonObject schema) => new()
    {
        ["type"] = "json_schema",
        ["schema"] = schema.DeepClone(),
    };

    public static JsonObject JsonSchema(StructuredOutputSchema schema) => JsonSchema(schema.ToJsonObject());

    // AOT- and trimming-safe: derives a JSON Schema from a source-generated JsonTypeInfo<T> via
    // System.Text.Json.Schema.JsonSchemaExporter. Not part of PY -- an extra convenience so a plain
    // typed model can drive ClaudeAgentOptions.OutputFormat without hand-writing a
    // StructuredOutputSchema.
    public static JsonObject JsonSchema<T>(JsonTypeInfo<T> jsonTypeInfo) => JsonSchema(JsonSchemaFor(jsonTypeInfo));

    public static JsonObject JsonSchemaFor<T>(JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var node = jsonTypeInfo.GetJsonSchemaAsNode();
        return node as JsonObject ?? throw new InvalidOperationException($"JsonSchemaExporter did not produce a JSON object schema for type '{typeof(T).FullName}'.");
    }
}
