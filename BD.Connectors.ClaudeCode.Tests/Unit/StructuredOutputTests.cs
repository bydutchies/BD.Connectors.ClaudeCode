using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

public sealed record WeatherReport(string City, double TemperatureCelsius, bool Rainy);

[JsonSerializable(typeof(WeatherReport))]
internal sealed partial class StructuredOutputTestJsonContext : JsonSerializerContext
{
}

// OutputFormats.JsonSchemaFor<T>/JsonSchema<T> and ResultMessageExtensions.GetStructuredOutput<T> --
// extra convenience helpers not present in PY, ported from CS
// ClaudeThread.DeserializeTypedResponse[WithReflection].
[Property("TestKind", "Unit")]
public class StructuredOutputTests
{
    private static ResultMessage BaseResult(JsonNode? structuredOutput) =>
        new("success", 100, 90, false, 1, "session-1") { StructuredOutput = structuredOutput };

    // JsonSchemaExporter marks a reference-type root as nullable by default (`"type":["object","null"]`)
    // -- a stock System.Text.Json default, not something this SDK controls -- so accept either shape.
    private static bool SchemaTypeIncludes(JsonNode? typeNode, string expected) => typeNode switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s == expected,
        JsonArray arr => arr.Any(n => n?.ToString() == expected),
        _ => false,
    };

    [Test]
    public async Task JsonSchemaFor_DerivesObjectSchemaFromTypeInfo()
    {
        var schema = OutputFormats.JsonSchemaFor(StructuredOutputTestJsonContext.Default.WeatherReport);

        await Assert.That(SchemaTypeIncludes(schema["type"], "object")).IsTrue();
        await Assert.That(schema["properties"]).IsNotNull();
        await Assert.That(schema["properties"]!["City"]).IsNotNull();
        await Assert.That(schema["properties"]!["TemperatureCelsius"]).IsNotNull();
        await Assert.That(schema["properties"]!["Rainy"]).IsNotNull();
    }

    [Test]
    public async Task JsonSchema_WrapsSchemaInJsonSchemaOutputFormat()
    {
        var format = OutputFormats.JsonSchema(StructuredOutputTestJsonContext.Default.WeatherReport);

        await Assert.That(format["type"]!.ToString()).IsEqualTo("json_schema");
        await Assert.That(SchemaTypeIncludes(format["schema"]!["type"], "object")).IsTrue();
    }

    [Test]
    public async Task GetStructuredOutput_TypeInfoOverload_DeserializesValue()
    {
        var node = new JsonObject { ["City"] = "Amsterdam", ["TemperatureCelsius"] = 12.5, ["Rainy"] = true };
        var result = BaseResult(node);

        var report = result.GetStructuredOutput(StructuredOutputTestJsonContext.Default.WeatherReport);

        await Assert.That(report.City).IsEqualTo("Amsterdam");
        await Assert.That(report.TemperatureCelsius).IsEqualTo(12.5);
        await Assert.That(report.Rainy).IsTrue();
    }

    [Test]
    public async Task GetStructuredOutput_NoStructuredOutput_Throws()
    {
        var result = BaseResult(null);

        await Assert.That(() => result.GetStructuredOutput(StructuredOutputTestJsonContext.Default.WeatherReport)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task GetStructuredOutput_MismatchedShape_ThrowsWrappingJsonException()
    {
        var node = JsonValue.Create("not an object");
        var result = BaseResult(node);

        var exception = await Assert.That(() => result.GetStructuredOutput(StructuredOutputTestJsonContext.Default.WeatherReport))
            .Throws<InvalidOperationException>();
        await Assert.That(exception!.InnerException).IsNotNull();
    }

    [Test]
    public async Task GetStructuredOutput_NullMessage_Throws()
    {
        ResultMessage message = null!;

        await Assert.That(() => message.GetStructuredOutput(StructuredOutputTestJsonContext.Default.WeatherReport)).Throws<ArgumentNullException>();
    }

    // The reflection-based convenience overload cannot succeed inside this solution's own test host:
    // referencing the AOT-compatible src project (`IsAotCompatible=true`) merges
    // `System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false` into the test host's
    // runtimeconfig.json, so `JsonSerializer.Deserialize<T>(node)` always throws here -- exactly the
    // scenario `[RequiresDynamicCode]`/`[RequiresUnreferencedCode]` warn callers about. This asserts
    // the overload fails loudly and clearly (a runtime `InvalidOperationException` naming the cause)
    // rather than a confusing crash, which is the only thing verifiable about this code path in an
    // AOT-compatible host; the `JsonTypeInfo<T>` overload above carries the real success-path coverage.
    [Test]
    public async Task GetStructuredOutput_ReflectionOverload_FailsClearlyWhenReflectionIsDisabled()
    {
        var node = new JsonObject { ["City"] = "Rotterdam", ["TemperatureCelsius"] = 9.0, ["Rainy"] = false };
        var result = BaseResult(node);

#pragma warning disable IL2026, IL3050
        var exception = await Assert.That(() => result.GetStructuredOutput<WeatherReport>()).Throws<InvalidOperationException>();
#pragma warning restore IL2026, IL3050

        await Assert.That(exception!.Message).Contains("Reflection-based serialization has been disabled");
    }
}
