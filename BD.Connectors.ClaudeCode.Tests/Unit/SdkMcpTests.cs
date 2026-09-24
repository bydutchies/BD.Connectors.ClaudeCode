using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BD.Connectors.ClaudeCode.Mcp;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// In PY, an Annotated[type, "description"] parameter carries its description into the schema; the
// typed-args C# overload gets the same via [Description].
public sealed record GreetArgs([property: Description("Who to greet")] string Name, int? Times);

[JsonSerializable(typeof(GreetArgs))]
internal sealed partial class SdkMcpTestJsonContext : JsonSerializerContext
{
}

// Coverage for SdkMcp.Tool()'s three overloads and CreateServer().
[Property("TestKind", "Unit")]
public class SdkMcpTests
{
    [Test]
    public async Task Tool_JsonObjectSchema_UsedAsIs_AndClonedFromCaller()
    {
        var original = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

        var tool = SdkMcp.Tool("t", "d", original, (_, _) => Task.FromResult(new McpToolResult([])));
        original["type"] = "mutated-after-the-fact";

        await Assert.That(tool.InputSchema["type"]!.GetValue<string>()).IsEqualTo("object");
    }

    [Test]
    public async Task Tool_DictSchema_MapsEverySupportedClrType()
    {
        var tool = SdkMcp.Tool(
            "calc",
            "d",
            new Dictionary<string, Type>
            {
                ["s"] = typeof(string),
                ["i"] = typeof(int),
                ["l"] = typeof(long),
                ["d"] = typeof(double),
                ["f"] = typeof(float),
                ["m"] = typeof(decimal),
                ["b"] = typeof(bool),
                ["arr"] = typeof(string[]),
                ["obj"] = typeof(object),
            },
            (_, _) => Task.FromResult(new McpToolResult([])));

        await Assert.That(tool.InputSchema["type"]!.GetValue<string>()).IsEqualTo("object");
        var props = tool.InputSchema["properties"]!.AsObject();
        await Assert.That(props["s"]!["type"]!.GetValue<string>()).IsEqualTo("string");
        await Assert.That(props["i"]!["type"]!.GetValue<string>()).IsEqualTo("integer");
        await Assert.That(props["l"]!["type"]!.GetValue<string>()).IsEqualTo("integer");
        await Assert.That(props["d"]!["type"]!.GetValue<string>()).IsEqualTo("number");
        await Assert.That(props["f"]!["type"]!.GetValue<string>()).IsEqualTo("number");
        await Assert.That(props["m"]!["type"]!.GetValue<string>()).IsEqualTo("number");
        await Assert.That(props["b"]!["type"]!.GetValue<string>()).IsEqualTo("boolean");
        await Assert.That(props["arr"]!["type"]!.GetValue<string>()).IsEqualTo("array");
        await Assert.That(props["obj"]!["type"]!.GetValue<string>()).IsEqualTo("object");

        var required = tool.InputSchema["required"]!.AsArray().Select(name => name!.GetValue<string>()).ToList();
        await Assert.That(required.Count).IsEqualTo(9);
    }

    [Test]
    public async Task Tool_Typed_DerivesSchemaWithDescription_AndDeserializesArguments()
    {
        GreetArgs? received = null;
        var tool = SdkMcp.Tool(
            "greet",
            "Greets someone",
            SdkMcpTestJsonContext.Default.GreetArgs,
            (args, _) =>
            {
                received = args;
                return Task.FromResult(new McpToolResult([new McpTextContent($"Hi {args.Name}")]));
            });

        await Assert.That(tool.InputSchema["properties"]!["Name"]!["description"]!.GetValue<string>()).IsEqualTo("Who to greet");

        var result = await tool.Handler(new JsonObject { ["Name"] = "Ada", ["Times"] = 2 }, CancellationToken.None);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!.Name).IsEqualTo("Ada");
        await Assert.That(received.Times).IsEqualTo(2);
        await Assert.That(((McpTextContent)result.Content[0]).Text).IsEqualTo("Hi Ada");
    }

    [Test]
    public async Task CreateServer_WrapsToolsInADispatcher_ListableByName()
    {
        var tool = SdkMcp.Tool(
            "add",
            "adds",
            new Dictionary<string, Type> { ["a"] = typeof(double), ["b"] = typeof(double) },
            (_, _) => Task.FromResult(new McpToolResult([new McpTextContent("3")])));

        var config = SdkMcp.CreateServer("calc", "2.0.0", [tool]);

        await Assert.That(config.Name).IsEqualTo("calc");
        var listResult = await config.Instance.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, CancellationToken.None);
        var tools = listResult!["result"]!["tools"]!.AsArray();
        await Assert.That(tools.Count).IsEqualTo(1);
        await Assert.That(tools[0]!["name"]!.GetValue<string>()).IsEqualTo("add");
    }

    [Test]
    public async Task CreateServer_NoTools_ReturnsEmptyToolsList()
    {
        var config = SdkMcp.CreateServer("empty");

        var listResult = await config.Instance.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, CancellationToken.None);

        await Assert.That(listResult!["result"]!["tools"]!.AsArray().Count).IsEqualTo(0);
    }
}
