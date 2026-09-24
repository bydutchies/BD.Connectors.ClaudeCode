using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Mcp;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// SdkMcpBridge (F5.3) is a thin wrapper: forward to ISdkMcpServer.HandleAsync, map an exception to a
// JSON-RPC error naming the failed message's own id, forward CloseAsync to DisposeAsync.
[Property("TestKind", "Unit")]
public class SdkMcpBridgeTests
{
    [Test]
    public async Task HandleAsync_DelegatesToServer_ReturnsItsResult()
    {
        var expected = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() };
        var server = new FakeSdkMcpServer { OnHandle = (_, _) => Task.FromResult<JsonObject?>(expected) };
        var bridge = new SdkMcpBridge("srv", server);

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" };
        var result = await bridge.HandleAsync(message);

        await Assert.That(result).IsSameReferenceAs(expected);
        await Assert.That(server.LastMessage).IsSameReferenceAs(message);
    }

    [Test]
    public async Task HandleAsync_NotificationReturnsNull_PassesThrough()
    {
        var server = new FakeSdkMcpServer { OnHandle = (_, _) => Task.FromResult<JsonObject?>(null) };
        var bridge = new SdkMcpBridge("srv", server);

        var result = await bridge.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task HandleAsync_ServerThrows_ReturnsInternalErrorNamingTheMessageId()
    {
        var server = new FakeSdkMcpServer { OnHandle = (_, _) => throw new InvalidOperationException("server exploded") };
        var bridge = new SdkMcpBridge("srv", server);

        var result = await bridge.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 42, ["method"] = "tools/call" });

        await Assert.That(result!["jsonrpc"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(result["id"]!.GetValue<int>()).IsEqualTo(42);
        await Assert.That(result["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32603);
        await Assert.That(result["error"]!["message"]!.GetValue<string>()).IsEqualTo("server exploded");
    }

    [Test]
    public async Task HandleAsync_ServerThrowsWithEmptyMessage_UsesExceptionTypeName()
    {
        var server = new FakeSdkMcpServer { OnHandle = (_, _) => throw new InvalidOperationException(string.Empty) };
        var bridge = new SdkMcpBridge("srv", server);

        var result = await bridge.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call" });

        await Assert.That(result!["error"]!["message"]!.GetValue<string>()).IsEqualTo(nameof(InvalidOperationException));
    }

    [Test]
    public async Task CloseAsync_DisposesTheServer()
    {
        var server = new FakeSdkMcpServer();
        var bridge = new SdkMcpBridge("srv", server);

        await bridge.CloseAsync();

        await Assert.That(server.Disposed).IsTrue();
    }

    private sealed class FakeSdkMcpServer : ISdkMcpServer
    {
        public Func<JsonObject, CancellationToken, Task<JsonObject?>>? OnHandle { get; init; }

        public JsonObject? LastMessage { get; private set; }

        public bool Disposed { get; private set; }

        public string Name => "srv";

        public Task<JsonObject?> HandleAsync(JsonObject jsonRpcMessage, CancellationToken cancellationToken)
        {
            LastMessage = jsonRpcMessage;
            return OnHandle is not null ? OnHandle(jsonRpcMessage, cancellationToken) : Task.FromResult<JsonObject?>(null);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
