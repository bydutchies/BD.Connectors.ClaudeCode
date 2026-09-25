using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace BD.Connectors.ClaudeCode.Options;

public static class OutputFormats
{
    private static readonly JsonSchemaExporterOptions _exporterOptions = new() { TreatNullObliviousAsNonNullable = true };

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

        // The CLI sends this schema as a tool input_schema, which the API rejects unless the root
        // type is exactly "object" -- the default exporter emits ["object", "null"] for reference types.
        var node = jsonTypeInfo.GetJsonSchemaAsNode(_exporterOptions);
        return node as JsonObject ?? throw new InvalidOperationException($"JsonSchemaExporter did not produce a JSON object schema for type '{typeof(T).FullName}'.");
    }
}
