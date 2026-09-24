using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Mcp;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Behavior-level tests for the SDK's own small MCP dispatcher: initialize version negotiation, ping,
// tools/list, tools/call (unknown tool/validation/handler exception/success),
// notifications/cancelled, unknown methods/notifications, and DisposeAsync's cancel-and-wait grace
// period.
[Property("TestKind", "Unit")]
public class SdkMcpToolServerTests
{
    [Test]
    public async Task HandleAsync_Initialize_SupportedVersion_IsEchoed()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);
        var message = InitializeMessage("2025-03-26");

        var result = await server.HandleAsync(message, CancellationToken.None);

        await Assert.That(result!["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-03-26");
        await Assert.That(result["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>()).IsFalse();
        await Assert.That(result["result"]!["serverInfo"]!["name"]!.GetValue<string>()).IsEqualTo("srv");
        await Assert.That(result["result"]!["serverInfo"]!["version"]!.GetValue<string>()).IsEqualTo("1.0.0");
        await Assert.That(result["id"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_Initialize_UnsupportedVersion_ReturnsLatestSupported()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);

        var result = await server.HandleAsync(InitializeMessage("1999-01-01"), CancellationToken.None);

        await Assert.That(result!["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-11-25");
    }

    [Test]
    public async Task HandleAsync_Initialize_NoParams_ReturnsLatestSupported()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize" };

        var result = await server.HandleAsync(message, CancellationToken.None);

        await Assert.That(result!["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-11-25");
    }

    [Test]
    public async Task HandleAsync_Ping_ReturnsEmptyResult()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);

        var result = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" }, CancellationToken.None);

        await Assert.That(result!["result"]!.AsObject().Count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_NotificationsInitialized_ReturnsNull()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);

        var result = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task HandleAsync_UnknownNotification_ReturnsNull()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);

        var result = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/whatever" }, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task HandleAsync_UnknownMethodWithId_ReturnsMethodNotFound()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);

        var result = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "resources/list" }, CancellationToken.None);

        await Assert.That(result!["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32601);
        await Assert.That(result["error"]!["message"]!.GetValue<string>()).IsEqualTo("Method not found");
        await Assert.That(result["id"]!.GetValue<int>()).IsEqualTo(7);
    }

    [Test]
    public async Task HandleAsync_ToolsList_IncludesSchemaAnnotationsAndMeta()
    {
        var tool = new SdkMcpTool(
            "greet",
            "Greets",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("name"),
            },
            (_, _) => Task.FromResult(new McpToolResult([new McpTextContent("hi")])),
            new ToolAnnotations(ReadOnlyHint: true, MaxResultSizeChars: 500));

        var server = new SdkMcpToolServer("srv", "2.0.0", [tool]);

        var result = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, CancellationToken.None);

        var tools = result!["result"]!["tools"]!.AsArray();
        await Assert.That(tools.Count).IsEqualTo(1);
        var entry = tools[0]!.AsObject();
        await Assert.That(entry["name"]!.GetValue<string>()).IsEqualTo("greet");
        await Assert.That(entry["description"]!.GetValue<string>()).IsEqualTo("Greets");
        await Assert.That(entry["inputSchema"]!["type"]!.GetValue<string>()).IsEqualTo("object");
        await Assert.That(entry["annotations"]!["readOnlyHint"]!.GetValue<bool>()).IsTrue();
        await Assert.That(entry["_meta"]!["anthropic/maxResultSizeChars"]!.GetValue<int>()).IsEqualTo(500);
    }

    [Test]
    public async Task HandleAsync_ToolsList_NoAnnotations_OmitsAnnotationsAndMeta()
    {
        var tool = new SdkMcpTool("plain", "no hints", new JsonObject { ["type"] = "object" }, (_, _) => Task.FromResult(new McpToolResult([])));
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var result = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, CancellationToken.None);

        var entry = result!["result"]!["tools"]![0]!.AsObject();
        await Assert.That(entry.ContainsKey("annotations")).IsFalse();
        await Assert.That(entry.ContainsKey("_meta")).IsFalse();
    }

    [Test]
    public async Task HandleAsync_ToolsCall_UnknownTool_ReturnsErrorResult()
    {
        var server = new SdkMcpToolServer("srv", "1.0.0", []);

        var result = await server.HandleAsync(ToolsCallMessage("missing"), CancellationToken.None);

        var toolResult = result!["result"]!.AsObject();
        await Assert.That(toolResult["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(toolResult["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("Tool 'missing' not found");
    }

    [Test]
    public async Task HandleAsync_ToolsCall_InvalidArguments_ReturnsValidationErrorResult()
    {
        var tool = new SdkMcpTool(
            "add",
            "adds",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = "number" } },
                ["required"] = new JsonArray("a"),
            },
            (_, _) => Task.FromResult(new McpToolResult([])));
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var result = await server.HandleAsync(ToolsCallMessage("add"), CancellationToken.None);

        var toolResult = result!["result"]!.AsObject();
        await Assert.That(toolResult["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(toolResult["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("Input validation error: 'a' is a required property");
    }

    [Test]
    public async Task HandleAsync_ToolsCall_HandlerThrows_ReturnsErrorResultWithExceptionMessage()
    {
        var tool = new SdkMcpTool("boom", "explodes", new JsonObject { ["type"] = "object" }, (_, _) => throw new InvalidOperationException("kaboom"));
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var result = await server.HandleAsync(ToolsCallMessage("boom"), CancellationToken.None);

        var toolResult = result!["result"]!.AsObject();
        await Assert.That(toolResult["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(toolResult["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("kaboom");
    }

    [Test]
    public async Task HandleAsync_ToolsCall_Success_ReturnsConvertedContent()
    {
        var tool = new SdkMcpTool("greet", "greets", new JsonObject { ["type"] = "object" }, (_, _) => Task.FromResult(new McpToolResult([new McpTextContent("Hello!")])));
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var result = await server.HandleAsync(ToolsCallMessage("greet"), CancellationToken.None);

        var toolResult = result!["result"]!.AsObject();
        await Assert.That(toolResult["isError"]!.GetValue<bool>()).IsFalse();
        await Assert.That(toolResult["content"]![0]!["type"]!.GetValue<string>()).IsEqualTo("text");
        await Assert.That(toolResult["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("Hello!");
    }

    [Test]
    public async Task HandleAsync_ToolsCall_ResourceLinkContent_FlattenedToText()
    {
        var tool = new SdkMcpTool(
            "link",
            "d",
            new JsonObject { ["type"] = "object" },
            (_, _) => Task.FromResult(new McpToolResult([new McpResourceLinkContent("Doc", "https://example.com", "A doc")])));
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var result = await server.HandleAsync(ToolsCallMessage("link"), CancellationToken.None);

        await Assert.That(result!["result"]!["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("Doc\nhttps://example.com\nA doc");
    }

    [Test]
    public async Task HandleAsync_ToolsCall_EmptyResourceLinkContent_FallsBackToPlaceholder()
    {
        var tool = new SdkMcpTool("link", "d", new JsonObject { ["type"] = "object" }, (_, _) => Task.FromResult(new McpToolResult([new McpResourceLinkContent()])));
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var result = await server.HandleAsync(ToolsCallMessage("link"), CancellationToken.None);

        await Assert.That(result!["result"]!["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("Resource link");
    }

    [Test]
    public async Task HandleAsync_ToolsCall_BinaryEmbeddedResource_SkippedFromContent()
    {
        var tool = new SdkMcpTool(
            "res",
            "d",
            new JsonObject { ["type"] = "object" },
            (_, _) => Task.FromResult(new McpToolResult([new McpEmbeddedResourceContent(), new McpTextContent("kept")])));
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var result = await server.HandleAsync(ToolsCallMessage("res"), CancellationToken.None);

        var content = result!["result"]!["content"]!.AsArray();
        await Assert.That(content.Count).IsEqualTo(1);
        await Assert.That(content[0]!["text"]!.GetValue<string>()).IsEqualTo("kept");
    }

    [Test]
    public async Task HandleAsync_NotificationsCancelled_CancelsInFlightToolCall_AnswersRequestCancelled()
    {
        var started = new TaskCompletionSource();
        var tool = new SdkMcpTool("wait", "waits", new JsonObject { ["type"] = "object" }, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new McpToolResult([]);
        });
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var callTask = server.HandleAsync(ToolsCallMessage("wait"), CancellationToken.None);
        await started.Task;

        var cancelMessage = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = 1 },
        };
        var cancelResult = await server.HandleAsync(cancelMessage, CancellationToken.None);
        await Assert.That(cancelResult).IsNull();

        var callResult = await callTask;
        await Assert.That(callResult!["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32800);
        await Assert.That(callResult["error"]!["message"]!.GetValue<string>()).IsEqualTo("Request cancelled");
    }

    [Test]
    public async Task DisposeAsync_CancelsActiveCalls_AndWaitsForThemToFinish()
    {
        var started = new TaskCompletionSource();
        var finished = false;
        var tool = new SdkMcpTool("wait", "waits", new JsonObject { ["type"] = "object" }, async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                finished = true;
            }

            return new McpToolResult([]);
        });
        var server = new SdkMcpToolServer("srv", "1.0.0", [tool]);

        var callTask = server.HandleAsync(ToolsCallMessage("wait"), CancellationToken.None);
        await started.Task;

        await server.DisposeAsync();

        await Assert.That(finished).IsTrue();
        var callResult = await callTask;
        await Assert.That(callResult!["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32800);
    }

    private static JsonObject InitializeMessage(string protocolVersion) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "initialize",
        ["params"] = new JsonObject { ["protocolVersion"] = protocolVersion },
    };

    private static JsonObject ToolsCallMessage(string toolName, JsonObject? arguments = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments ?? new JsonObject() },
    };
}
