using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Internal;

// Small, dependency-free JSON Schema subset validator for SDK MCP tool arguments. Supports `type`
// (single or array of names, including "integer" as a refinement of "number"), `required`,
// `properties` (recursive), `items`, `enum`, `anyOf` and `additionalProperties: false`. Unknown
// keywords are ignored rather than rejected -- this mirrors PY's `jsonschema.validate` only for the
// keywords the SDK's own schema derivation actually emits, not full JSON Schema Draft 2020-12 (a
// dependency-based validator could replace this later if that is needed). Returns the first
// validation failure as a human-readable message, or null when the instance is valid.
internal static class JsonSchemaValidator
{
    public static string? Validate(JsonNode? instance, JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return ValidateNode(instance, schema, "value");
    }

    private static string? ValidateNode(JsonNode? instance, JsonObject schema, string path)
    {
        if (schema.TryGetPropertyValue("anyOf", out var anyOfNode) && anyOfNode is JsonArray { Count: > 0 } anyOf)
        {
            string? firstError = null;
            foreach (var candidate in anyOf)
            {
                if (candidate is not JsonObject candidateSchema)
                {
                    continue;
                }

                var error = ValidateNode(instance, candidateSchema, path);
                if (error is null)
                {
                    firstError = null;
                    break;
                }

                firstError ??= error;
            }

            if (firstError is not null)
            {
                return firstError;
            }
        }

        if (schema.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumValues
            && !enumValues.Any(candidate => JsonNode.DeepEquals(candidate, instance)))
        {
            return $"{path} is not one of the allowed values";
        }

        if (schema.TryGetPropertyValue("type", out var typeNode) && typeNode is not null)
        {
            var typeError = ValidateType(instance, typeNode, path);
            if (typeError is not null)
            {
                return typeError;
            }
        }

        if (instance is JsonObject instanceObject)
        {
            var propertyError = ValidateProperties(instanceObject, schema, path);
            if (propertyError is not null)
            {
                return propertyError;
            }
        }
        else if (instance is JsonArray instanceArray
            && schema.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonObject itemsSchema)
        {
            for (var index = 0; index < instanceArray.Count; index++)
            {
                var itemError = ValidateNode(instanceArray[index], itemsSchema, $"{path}[{index}]");
                if (itemError is not null)
                {
                    return itemError;
                }
            }
        }

        return null;
    }

    private static string? ValidateProperties(JsonObject instanceObject, JsonObject schema, string path)
    {
        if (schema.TryGetPropertyValue("required", out var requiredNode) && requiredNode is JsonArray required)
        {
            foreach (var requiredName in required)
            {
                var name = requiredName?.GetValue<string>();
                if (!string.IsNullOrEmpty(name) && !instanceObject.ContainsKey(name))
                {
                    return $"'{name}' is a required property";
                }
            }
        }

        var hasPropertiesSchema = schema.TryGetPropertyValue("properties", out var propertiesNode) && propertiesNode is JsonObject;
        var properties = hasPropertiesSchema ? (JsonObject)propertiesNode! : null;
        var additionalPropertiesAllowed = !(schema.TryGetPropertyValue("additionalProperties", out var additionalNode)
            && additionalNode is JsonValue additionalValue
            && additionalValue.GetValueKind() == System.Text.Json.JsonValueKind.False);

        foreach (var (key, value) in instanceObject)
        {
            if (properties is not null && properties.TryGetPropertyValue(key, out var propertySchemaNode) && propertySchemaNode is JsonObject propertySchema)
            {
                var error = ValidateNode(value, propertySchema, $"{path}.{key}");
                if (error is not null)
                {
                    return error;
                }
            }
            else if (!additionalPropertiesAllowed)
            {
                return $"Additional property '{key}' is not allowed";
            }
        }

        return null;
    }

    private static string? ValidateType(JsonNode? instance, JsonNode typeNode, string path)
    {
        var allowedTypes = typeNode switch
        {
            JsonArray array => array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrEmpty(item)).ToList()!,
            JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String => [value.GetValue<string>()],
            _ => null,
        };

        if (allowedTypes is not { Count: > 0 })
        {
            return null;
        }

        var actualType = JsonTypeName(instance);
        var matches = allowedTypes.Contains(actualType)
            || (actualType == "integer" && allowedTypes.Contains("number"));

        return matches ? null : $"{path} is not of type {string.Join("/", allowedTypes)} (got {actualType})";
    }

    // Uses GetValueKind() + the value's own JSON text rather than TryGetValue<T>: a JsonValue built
    // from a CLR literal (as opposed to parsed from text) only converts through TryGetValue<T> for
    // an exact type match, so TryGetValue<double>() on a CLR-boxed `int` fails even though the value
    // is clearly numeric. Sniffing the rendered text works identically for both origins.
    private static string JsonTypeName(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Object => "object",
            System.Text.Json.JsonValueKind.Array => "array",
            System.Text.Json.JsonValueKind.String => "string",
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
            System.Text.Json.JsonValueKind.Number => IsIntegerNumber(node) ? "integer" : "number",
            _ => "null",
        };
    }

    private static bool IsIntegerNumber(JsonNode node)
    {
        var text = node.ToJsonString();
        return !text.Contains('.') && !text.Contains('e') && !text.Contains('E');
    }
}
