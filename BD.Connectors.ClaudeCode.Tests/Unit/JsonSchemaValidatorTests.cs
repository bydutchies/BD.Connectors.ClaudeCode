using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Coverage for the JSON Schema subset JsonSchemaValidator (F5.2) supports: type (incl. "integer" as
// a refinement of "number"), required, properties, items, enum, anyOf, additionalProperties: false,
// and that unknown keywords are ignored rather than rejected.
[Property("TestKind", "Unit")]
public class JsonSchemaValidatorTests
{
    // Numeric/boolean instance values are round-tripped through JSON text: a JsonValue built
    // directly from a CLR literal (e.g. `["a"] = 5`) only satisfies TryGetValue<T>() for an exact
    // type match, unlike a JsonElement-backed value parsed from real wire text (which is what every
    // production caller actually validates against) -- see ClaudeSdkClientTests for the same pitfall.
    private static JsonObject Reparse(JsonObject obj) => (JsonObject)JsonNode.Parse(obj.ToJsonString())!;

    [Test]
    public async Task Validate_TypeMismatch_ReturnsError()
    {
        var schema = new JsonObject { ["type"] = "string" };
        var instance = Reparse(new JsonObject { ["v"] = 5 })["v"];

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_TypeMatch_ReturnsNull()
    {
        var schema = new JsonObject { ["type"] = "string" };

        var error = JsonSchemaValidator.Validate(JsonValue.Create("hello"), schema);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_IntegerInstance_SatisfiesNumberType()
    {
        var schema = new JsonObject { ["type"] = "number" };
        var instance = Reparse(new JsonObject { ["v"] = 5 })["v"];

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_FractionalInstance_FailsIntegerType()
    {
        var schema = new JsonObject { ["type"] = "integer" };
        var instance = Reparse(new JsonObject { ["v"] = 5.5 })["v"];

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_TypeArray_MatchesAnyListedType()
    {
        var schema = new JsonObject { ["type"] = new JsonArray("string", "null") };

        await Assert.That(JsonSchemaValidator.Validate(JsonValue.Create("x"), schema)).IsNull();
        await Assert.That(JsonSchemaValidator.Validate(null, schema)).IsNull();
        var mismatch = JsonSchemaValidator.Validate(Reparse(new JsonObject { ["v"] = 1 })["v"], schema);
        await Assert.That(mismatch).IsNotNull();
    }

    [Test]
    public async Task Validate_Required_MissingProperty_ReturnsError()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("name"),
        };

        var error = JsonSchemaValidator.Validate(new JsonObject(), schema);

        await Assert.That(error).IsEqualTo("'name' is a required property");
    }

    [Test]
    public async Task Validate_Required_PresentProperty_ReturnsNull()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("name"),
        };

        var error = JsonSchemaValidator.Validate(new JsonObject { ["name"] = "Ada" }, schema);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_Properties_RecursiveTypeMismatch_ReturnsNestedError()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["age"] = new JsonObject { ["type"] = "string" } },
        };
        var instance = Reparse(new JsonObject { ["age"] = 42 });

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Contains("age", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Validate_Items_RecursiveTypeMismatch_ReturnsError()
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        var instance = Reparse(new JsonObject { ["v"] = new JsonArray("ok", 5) })["v"];

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_Items_AllMatch_ReturnsNull()
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };

        var error = JsonSchemaValidator.Validate(new JsonArray("a", "b"), schema);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_Enum_ValueNotAllowed_ReturnsError()
    {
        var schema = new JsonObject { ["enum"] = new JsonArray("red", "green", "blue") };

        var error = JsonSchemaValidator.Validate(JsonValue.Create("purple"), schema);

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_Enum_ValueAllowed_ReturnsNull()
    {
        var schema = new JsonObject { ["enum"] = new JsonArray("red", "green", "blue") };

        var error = JsonSchemaValidator.Validate(JsonValue.Create("green"), schema);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_AnyOf_MatchesOneBranch_ReturnsNull()
    {
        var schema = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "number" }),
        };
        var instance = Reparse(new JsonObject { ["v"] = 5 })["v"];

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_AnyOf_MatchesNoBranch_ReturnsError()
    {
        var schema = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "boolean" }),
        };
        var instance = Reparse(new JsonObject { ["v"] = 5 })["v"];

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_AdditionalPropertiesFalse_RejectsUndeclaredProperty()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = "string" } },
            ["additionalProperties"] = false,
        };
        var instance = new JsonObject { ["a"] = "x", ["b"] = "y" };

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsEqualTo("Additional property 'b' is not allowed");
    }

    [Test]
    public async Task Validate_AdditionalPropertiesOmitted_AllowsUndeclaredProperty()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = "string" } },
        };
        var instance = new JsonObject { ["a"] = "x", ["b"] = "y" };

        var error = JsonSchemaValidator.Validate(instance, schema);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_UnknownKeyword_IsIgnored()
    {
        var schema = new JsonObject { ["type"] = "string", ["format"] = "email", ["minLength"] = 3 };

        var error = JsonSchemaValidator.Validate(JsonValue.Create("x"), schema);

        await Assert.That(error).IsNull();
    }
}
