using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Mcp;

// PY's `tool()` decorator + `create_sdk_mcp_server()`. C# has no decorator syntax, so Tool(...)
// builds an SdkMcpTool directly instead of returning one from a wrapped function.
public static class SdkMcp
{
    private static readonly Dictionary<Type, string> _primitiveTypeTokens = new()
    {
        [typeof(string)] = "string",
        [typeof(int)] = "integer",
        [typeof(long)] = "integer",
        [typeof(double)] = "number",
        [typeof(float)] = "number",
        [typeof(decimal)] = "number",
        [typeof(bool)] = "boolean",
    };

    // Already a full schema (JsonObject-form input): used as-is, matching PY's "has string 'type' and
    // 'properties'" passthrough check -- the dict-of-python-types case that check also guards against
    // has no C# equivalent here since that shape is its own overload below.
    public static SdkMcpTool Tool(
        string name,
        string description,
        JsonObject inputSchema,
        Func<JsonObject, CancellationToken, Task<McpToolResult>> handler,
        ToolAnnotations? annotations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(handler);

        return new SdkMcpTool(name, description, (JsonObject)inputSchema.DeepClone(), handler, annotations);
    }

    // PY's dict-of-types form (`{"a": float}`): a JSON Schema is derived, one property per entry, all
    // required. Supported CLR types: string/int/long/double/float/decimal/bool/array/object;
    // anything else falls back to "object" the same way PY's `_python_type_to_json_schema` falls back
    // to "string" for a type it does not recognize -- except C# has no natural "always safe" fallback
    // type the way PY's `str` is, so unknown/complex CLR types become the most permissive JSON type
    // instead of the least.
    public static SdkMcpTool Tool(
        string name,
        string description,
        IReadOnlyDictionary<string, Type> parameters,
        Func<JsonObject, CancellationToken, Task<McpToolResult>> handler,
        ToolAnnotations? annotations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(handler);

        return new SdkMcpTool(name, description, BuildDictSchema(parameters), handler, annotations);
    }

    // Typed args via a source-generated JsonTypeInfo<TArgs> (AOT-safe): the schema comes from
    // JsonSchemaExporter, and [Description] on a property becomes the schema property's
    // "description". The wrapped handler deserializes the raw JSON-RPC arguments into TArgs before
    // calling the caller's handler.
    public static SdkMcpTool Tool<TArgs>(
        string name,
        string description,
        JsonTypeInfo<TArgs> argsTypeInfo,
        Func<TArgs, CancellationToken, Task<McpToolResult>> handler,
        ToolAnnotations? annotations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(argsTypeInfo);
        ArgumentNullException.ThrowIfNull(handler);

        var schema = (JsonObject)argsTypeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions { TransformSchemaNode = ApplyDescriptionAttribute });

        async Task<McpToolResult> WrappedHandler(JsonObject input, CancellationToken cancellationToken)
        {
            var args = System.Text.Json.JsonSerializer.Deserialize(input, argsTypeInfo)
                ?? throw new ArgumentException($"Could not deserialize arguments for tool '{name}'.");
            return await handler(args, cancellationToken).ConfigureAwait(false);
        }

        return new SdkMcpTool(name, description, schema, WrappedHandler, annotations);
    }

    public static McpSdkServerConfig CreateServer(string name, string version = "1.0.0", IReadOnlyList<SdkMcpTool>? tools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(version);

        var server = new SdkMcpToolServer(name, version, tools ?? []);
        return new McpSdkServerConfig(name, server);
    }

    private static JsonNode ApplyDescriptionAttribute(JsonSchemaExporterContext context, JsonNode schema)
    {
        var description = context.PropertyInfo?.AttributeProvider?
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault();

        if (description is not null && schema is JsonObject schemaObject)
        {
            schemaObject["description"] = description.Description;
        }

        return schema;
    }

    private static JsonObject BuildDictSchema(IReadOnlyDictionary<string, Type> parameters)
    {
        var properties = new JsonObject();
        foreach (var (parameterName, parameterType) in parameters)
        {
            properties[parameterName] = TypeToJsonSchema(parameterType);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. parameters.Keys.Select(key => (JsonNode?)JsonValue.Create(key))]),
        };
    }

    private static JsonObject TypeToJsonSchema(Type type)
    {
        if (_primitiveTypeTokens.TryGetValue(type, out var token))
        {
            return new JsonObject { ["type"] = token };
        }

        if (type != typeof(string) && (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))
        {
            return new JsonObject { ["type"] = "array" };
        }

        return new JsonObject { ["type"] = "object" };
    }
}
