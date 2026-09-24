using System.Linq.Expressions;
using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Options;

public enum StructuredOutputSchemaType
{
    PlainText,
    Numeric,
    Flag,
    Map,
    Sequence,
}

public sealed record StructuredOutputSchema
{
    private const string MissingRequiredSchemaPropertyMessagePrefix = "Required property";
    private const string MissingRequiredSchemaPropertyMessageSuffix = "must exist in schema properties.";
    private const string PropertyMappingRequiredMessage = "At least one property mapping is required.";
    private const string UnsupportedSchemaTypeMessagePrefix = "Unsupported schema type:";
    private const string ArraySchemaRequiresItemsMessage = "Array schema requires Items.";
    private const string ObjectSchemaRequiresPropertiesMessage = "Object schema requires Properties.";
    private const string InvalidPropertySelectorMessage = "Property selector must point to a model property.";
    private const string Space = " ";
    private const string MessageQuote = "'";
    private const string MessageSuffix = ".";

    public required StructuredOutputSchemaType Type { get; init; }

    public IReadOnlyDictionary<string, StructuredOutputSchema>? Properties { get; init; }

    public IReadOnlyList<string>? Required { get; init; }

    public StructuredOutputSchema? Items { get; init; }

    public bool? AdditionalProperties { get; init; }

    public static StructuredOutputSchema PlainText()
    {
        return new StructuredOutputSchema { Type = StructuredOutputSchemaType.PlainText };
    }

    public static StructuredOutputSchema Numeric()
    {
        return new StructuredOutputSchema { Type = StructuredOutputSchemaType.Numeric };
    }

    public static StructuredOutputSchema Flag()
    {
        return new StructuredOutputSchema { Type = StructuredOutputSchemaType.Flag };
    }

    public static StructuredOutputSchema Sequence(StructuredOutputSchema items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new StructuredOutputSchema
        {
            Type = StructuredOutputSchemaType.Sequence,
            Items = items,
        };
    }

    public static StructuredOutputSchema Map(
        IReadOnlyDictionary<string, StructuredOutputSchema> properties,
        IReadOnlyList<string>? required = null,
        bool? additionalProperties = null)
    {
        ArgumentNullException.ThrowIfNull(properties);

        foreach (var propertyName in properties.Keys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        }

        if (required is not null)
        {
            foreach (var requiredProperty in required)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(requiredProperty);
                if (!properties.ContainsKey(requiredProperty))
                {
                    throw new ArgumentException(
                        string.Concat(
                            MissingRequiredSchemaPropertyMessagePrefix,
                            Space,
                            MessageQuote,
                            requiredProperty,
                            MessageQuote,
                            Space,
                            MissingRequiredSchemaPropertyMessageSuffix),
                        nameof(required));
                }
            }
        }

        return new StructuredOutputSchema
        {
            Type = StructuredOutputSchemaType.Map,
            Properties = new Dictionary<string, StructuredOutputSchema>(properties),
            Required = required?.ToArray(),
            AdditionalProperties = additionalProperties,
        };
    }

    public static StructuredOutputSchema Map<TModel>(
        bool? additionalProperties = null,
        params (Expression<Func<TModel, object?>> Property, StructuredOutputSchema Schema)[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length == 0)
        {
            throw new ArgumentException(PropertyMappingRequiredMessage, nameof(properties));
        }

        var mappedProperties = new Dictionary<string, StructuredOutputSchema>(StringComparer.Ordinal);
        var requiredProperties = new List<string>(properties.Length);

        foreach (var (propertyExpression, schema) in properties)
        {
            ArgumentNullException.ThrowIfNull(propertyExpression);
            ArgumentNullException.ThrowIfNull(schema);

            var propertyName = ResolvePropertyName(propertyExpression.Body);
            mappedProperties[propertyName] = schema;
            requiredProperties.Add(propertyName);
        }

        return Map(mappedProperties, requiredProperties, additionalProperties);
    }

    public JsonObject ToJsonObject()
    {
        return Type switch
        {
            StructuredOutputSchemaType.PlainText => PrimitiveSchema(JsonTypeTokens.String),
            StructuredOutputSchemaType.Numeric => PrimitiveSchema(JsonTypeTokens.Number),
            StructuredOutputSchemaType.Flag => PrimitiveSchema(JsonTypeTokens.Boolean),
            StructuredOutputSchemaType.Sequence => ArraySchema(),
            StructuredOutputSchemaType.Map => ObjectSchema(),
            _ => throw new InvalidOperationException(string.Concat(UnsupportedSchemaTypeMessagePrefix, Space, Type.ToString(), MessageSuffix)),
        };
    }

    private static JsonObject PrimitiveSchema(string typeToken)
    {
        return new JsonObject { [JsonTypeTokens.Type] = typeToken };
    }

    private JsonObject ArraySchema()
    {
        if (Items is null)
        {
            throw new InvalidOperationException(ArraySchemaRequiresItemsMessage);
        }

        return new JsonObject
        {
            [JsonTypeTokens.Type] = JsonTypeTokens.Array,
            [JsonTypeTokens.Items] = Items.ToJsonObject(),
        };
    }

    private JsonObject ObjectSchema()
    {
        if (Properties is null)
        {
            throw new InvalidOperationException(ObjectSchemaRequiresPropertiesMessage);
        }

        var properties = new JsonObject();
        foreach (var (name, schema) in Properties)
        {
            properties[name] = schema.ToJsonObject();
        }

        var result = new JsonObject
        {
            [JsonTypeTokens.Type] = JsonTypeTokens.Object,
            [JsonTypeTokens.Properties] = properties,
        };

        if (Required is { Count: > 0 })
        {
            result[JsonTypeTokens.Required] = new JsonArray(Required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        }

        if (AdditionalProperties.HasValue)
        {
            result[JsonTypeTokens.AdditionalProperties] = AdditionalProperties.Value;
        }

        return result;
    }

    private static string ResolvePropertyName(Expression expression)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert } unaryExpression)
        {
            expression = unaryExpression.Operand;
        }

        if (expression is MemberExpression { Member.MemberType: System.Reflection.MemberTypes.Property } memberExpression)
        {
            return memberExpression.Member.Name;
        }

        throw new ArgumentException(InvalidPropertySelectorMessage);
    }

    private static class JsonTypeTokens
    {
        public const string Type = "type";
        public const string String = "string";
        public const string Number = "number";
        public const string Boolean = "boolean";
        public const string Object = "object";
        public const string Array = "array";
        public const string Properties = "properties";
        public const string Required = "required";
        public const string AdditionalProperties = "additionalProperties";
        public const string Items = "items";
    }
}
