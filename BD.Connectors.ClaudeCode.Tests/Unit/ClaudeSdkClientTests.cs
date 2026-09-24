using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Tests.Fakes;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Behavior-level port of the relevant parts of PY's streaming-client tests against
// ClaudeSdkClient, plus a couple of scenarios called out explicitly (two query() turns on one
// connection; InterruptAsync during a long turn yields a ResultMessage with an aborted_*
// TerminalReason).
[Property("TestKind", "Unit")]
public class ClaudeSdkClientTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);

    private static async Task<(ClaudeSdkClient Client, ScriptedTransport Transport)> ConnectedClientAsync(
        ClaudeAgentOptions? options = null,
        string? prompt = null)
    {
        var transport = new ScriptedTransport { AutoRespondInitialize = true };
        var client = new ClaudeSdkClient(options, transport);
        await client.ConnectAsync(prompt);
        return (client, transport);
    }

    [Test]
    public async Task Constructor_NoOptions_DefaultsToNewOptions()
    {
        var client = new ClaudeSdkClient();

        await Assert.That(client.Options).IsNotNull();
    }

    [Test]
    public async Task ConnectAsync_NoPrompt_InitializesOnly_NoUserMessageWritten()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        await Assert.That(transport.Writes.Count).IsEqualTo(1);
        await Assert.That(transport.Writes[0]["request"]!["subtype"]!.GetValue<string>()).IsEqualTo("initialize");
        await Assert.That(client.GetServerInfo()).IsNotNull();
    }

    [Test]
    public async Task ConnectAsync_StringPrompt_SendsInitializeThenUserMessageWithDefaultSessionId()
    {
        var (client, transport) = await ConnectedClientAsync(prompt: "Hello");
        await using var _ = client;

        var userWrite = await transport.WaitForWriteAsync(w => w["type"]?.GetValue<string>() == "user", ShortWait);
        await Assert.That(userWrite["session_id"]!.GetValue<string>()).IsEqualTo("default");
        await Assert.That(userWrite["message"]!["role"]!.GetValue<string>()).IsEqualTo("user");
        await Assert.That(userWrite["message"]!["content"]!.GetValue<string>()).IsEqualTo("Hello");
        await Assert.That(userWrite["parent_tool_use_id"]).IsNull();
    }

    [Test]
    public async Task ConnectAsync_StreamPrompt_ForwardsEachMessage()
    {
        var transport = new ScriptedTransport { AutoRespondInitialize = true };
        var client = new ClaudeSdkClient(transport: transport);
        await using var _ = client;

        static async IAsyncEnumerable<JsonObject> Prompts()
        {
            yield return new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "one" } };
            await Task.Yield();
            yield return new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "two" } };
        }

        await client.ConnectAsync(Prompts());

        await transport.WaitForWriteAsync(w => w["message"]?["content"]?.GetValue<string>() == "two", ShortWait);
        await Assert.That(transport.Writes.Count(w => w["type"]?.GetValue<string>() == "user")).IsEqualTo(2);
    }

    [Test]
    public async Task ConnectAsync_TransportFails_ClosesAndRethrows()
    {
        var client = new ClaudeSdkClient(transport: new ThrowingConnectTransport());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.ConnectAsync());
        await Assert.That(client.GetServerInfo()).IsNull();
    }

    [Test]
    public async Task QueryAsync_String_NotConnected_ThrowsCliConnectionException()
    {
        var client = new ClaudeSdkClient();

        var exception = await Assert.ThrowsAsync<CliConnectionException>(async () => await client.QueryAsync("Hi"));
        await Assert.That(exception!.Message).IsEqualTo("Not connected. Call connect() first.");
    }

    [Test]
    public async Task QueryAsync_Stream_NotConnected_ThrowsCliConnectionException()
    {
        var client = new ClaudeSdkClient();

        static async IAsyncEnumerable<JsonObject> Prompts()
        {
            await Task.Yield();
            yield return new JsonObject { ["type"] = "user" };
        }

        await Assert.ThrowsAsync<CliConnectionException>(async () => await client.QueryAsync(Prompts()));
    }

    [Test]
    public async Task QueryAsync_String_SendsUserMessageWithGivenSessionId()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        await client.QueryAsync("Second turn", sessionId: "session-42");

        var write = await transport.WaitForWriteAsync(w => w["type"]?.GetValue<string>() == "user", ShortWait);
        await Assert.That(write["session_id"]!.GetValue<string>()).IsEqualTo("session-42");
        await Assert.That(write["message"]!["content"]!.GetValue<string>()).IsEqualTo("Second turn");
    }

    [Test]
    public async Task QueryAsync_Stream_OnlySetsSessionIdWhenMissing()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        static async IAsyncEnumerable<JsonObject> Prompts()
        {
            yield return new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "no-session" } };
            await Task.Yield();
            yield return new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "has-session" },
                ["session_id"] = "already-set",
            };
        }

        await client.QueryAsync(Prompts(), sessionId: "default-session");

        var noSession = await transport.WaitForWriteAsync(w => w["message"]?["content"]?.GetValue<string>() == "no-session", ShortWait);
        await Assert.That(noSession["session_id"]!.GetValue<string>()).IsEqualTo("default-session");

        var hasSession = await transport.WaitForWriteAsync(w => w["message"]?["content"]?.GetValue<string>() == "has-session", ShortWait);
        await Assert.That(hasSession["session_id"]!.GetValue<string>()).IsEqualTo("already-set");
    }

    [Test]
    public async Task ReceiveMessagesAsync_NotConnected_ThrowsCliConnectionException()
    {
        var client = new ClaudeSdkClient();

        await Assert.ThrowsAsync<CliConnectionException>(async () =>
        {
            await foreach (var _ in client.ReceiveMessagesAsync())
            {
            }
        });
    }

    [Test]
    public async Task ReceiveResponseAsync_StopsAfterFirstResultMessage()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        transport.Emit(AssistantMessageJson("Hi there"));
        transport.Emit(ResultMessageJson());
        transport.Emit(AssistantMessageJson("Should not be seen"));

        var messages = new List<Message>();
        await foreach (var message in client.ReceiveResponseAsync())
        {
            messages.Add(message);
        }

        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages[0]).IsTypeOf<AssistantMessage>();
        await Assert.That(messages[1]).IsTypeOf<ResultMessage>();
    }

    [Test]
    public async Task TwoQueryAsyncTurns_OnOneConnection_YieldTwoResponses()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        await client.QueryAsync("First");
        await transport.WaitForWriteAsync(w => w["message"]?["content"]?.GetValue<string>() == "First", ShortWait);
        transport.Emit(ResultMessageJson("session-1"));

        var firstResponse = new List<Message>();
        await foreach (var message in client.ReceiveResponseAsync())
        {
            firstResponse.Add(message);
        }

        await Assert.That(firstResponse.Count).IsEqualTo(1);
        await Assert.That(firstResponse[0]).IsTypeOf<ResultMessage>();

        await client.QueryAsync("Second");
        await transport.WaitForWriteAsync(w => w["message"]?["content"]?.GetValue<string>() == "Second", ShortWait);
        transport.Emit(AssistantMessageJson("Second reply"));
        transport.Emit(ResultMessageJson("session-1"));

        var secondResponse = new List<Message>();
        await foreach (var message in client.ReceiveResponseAsync())
        {
            secondResponse.Add(message);
        }

        await Assert.That(secondResponse.Count).IsEqualTo(2);
        await Assert.That(secondResponse[0]).IsTypeOf<AssistantMessage>();
        await Assert.That(secondResponse[1]).IsTypeOf<ResultMessage>();
    }

    [Test]
    public async Task InterruptAsync_DuringLongTurn_YieldsResultMessageWithAbortedTerminalReason()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        await client.QueryAsync("Do something slow");
        await transport.WaitForWriteAsync(w => w["message"]?["content"]?.GetValue<string>() == "Do something slow", ShortWait);

        var interruptTask = client.InterruptAsync();
        var interruptWrite = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "interrupt", ShortWait);
        await AckAsync(transport, interruptWrite);
        await interruptTask;

        transport.Emit(ResultMessageJson("session-1", terminalReason: "aborted_by_user"));

        var response = new List<Message>();
        await foreach (var message in client.ReceiveResponseAsync())
        {
            response.Add(message);
        }

        await Assert.That(response.Count).IsEqualTo(1);
        var result = (ResultMessage)response[0];
        await Assert.That(result.TerminalReason).IsNotNull();
        await Assert.That(result.TerminalReason!.StartsWith("aborted", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task InterruptAsync_NotConnected_ThrowsCliConnectionException()
    {
        var client = new ClaudeSdkClient();

        await Assert.ThrowsAsync<CliConnectionException>(async () => await client.InterruptAsync());
    }

    [Test]
    public async Task SetPermissionModeAsync_SendsWireValue()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var task = client.SetPermissionModeAsync(PermissionMode.AcceptEdits);
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "set_permission_mode", ShortWait);
        await AckAsync(transport, write);
        await task;

        await Assert.That(write["request"]!["mode"]!.GetValue<string>()).IsEqualTo("acceptEdits");
    }

    [Test]
    public async Task SetModelAsync_NullModel_SendsNullModelField()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var task = client.SetModelAsync(null);
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "set_model", ShortWait);
        await AckAsync(transport, write);
        await task;

        await Assert.That(write["request"]!.AsObject().ContainsKey("model")).IsTrue();
        await Assert.That(write["request"]!["model"]).IsNull();
    }

    [Test]
    public async Task RewindFilesAsync_SendsUserMessageId()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var task = client.RewindFilesAsync("msg-uuid-1");
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "rewind_files", ShortWait);
        await AckAsync(transport, write);
        await task;

        await Assert.That(write["request"]!["user_message_id"]!.GetValue<string>()).IsEqualTo("msg-uuid-1");
    }

    [Test]
    public async Task ReconnectMcpServerAsync_SendsServerName()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var task = client.ReconnectMcpServerAsync("my-server");
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "mcp_reconnect", ShortWait);
        await AckAsync(transport, write);
        await task;

        await Assert.That(write["request"]!["serverName"]!.GetValue<string>()).IsEqualTo("my-server");
    }

    [Test]
    public async Task ToggleMcpServerAsync_SendsServerNameAndEnabled()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var task = client.ToggleMcpServerAsync("my-server", enabled: false);
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "mcp_toggle", ShortWait);
        await AckAsync(transport, write);
        await task;

        await Assert.That(write["request"]!["serverName"]!.GetValue<string>()).IsEqualTo("my-server");
        await Assert.That(write["request"]!["enabled"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task StopTaskAsync_SendsTaskId()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var task = client.StopTaskAsync("task-abc123");
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "stop_task", ShortWait);
        await AckAsync(transport, write);
        await task;

        await Assert.That(write["request"]!["task_id"]!.GetValue<string>()).IsEqualTo("task-abc123");
    }

    [Test]
    public async Task GetMcpStatusAsync_MapsResponseToTypedRecord()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var responseTask = client.GetMcpStatusAsync();
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "mcp_status", ShortWait);
        await AckAsync(transport, write, new JsonObject
        {
            ["mcpServers"] = new JsonArray(new JsonObject { ["name"] = "calc", ["status"] = "connected" }),
        });

        var status = await responseTask;
        await Assert.That(status.McpServers.Count).IsEqualTo(1);
        await Assert.That(status.McpServers[0].Name).IsEqualTo("calc");
        await Assert.That(status.McpServers[0].Status).IsEqualTo(McpServerConnectionStatuses.Connected);
    }

    [Test]
    public async Task GetContextUsageAsync_MapsResponseToTypedRecord()
    {
        var (client, transport) = await ConnectedClientAsync();
        await using var _ = client;

        var responseTask = client.GetContextUsageAsync();
        var write = await transport.WaitForWriteAsync(w => w["request"]?["subtype"]?.GetValue<string>() == "get_context_usage", ShortWait);
        await AckAsync(transport, write, new JsonObject
        {
            ["totalTokens"] = 100,
            ["maxTokens"] = 1000,
            ["percentage"] = 10.0,
            ["model"] = "claude-test",
            ["categories"] = new JsonArray(),
        });

        var usage = await responseTask;
        await Assert.That(usage.TotalTokens).IsEqualTo(100);
        await Assert.That(usage.MaxTokens).IsEqualTo(1000);
        await Assert.That(usage.Model).IsEqualTo("claude-test");
    }

    [Test]
    public async Task GetServerInfo_NotConnected_ReturnsNull()
    {
        var client = new ClaudeSdkClient();

        await Assert.That(client.GetServerInfo()).IsNull();
    }

    [Test]
    public async Task DisconnectAsync_ClosesTransport()
    {
        var (client, transport) = await ConnectedClientAsync();

        await client.DisconnectAsync();

        await Assert.That(transport.CloseCalled).IsTrue();
        await Assert.That(client.GetServerInfo()).IsNull();
    }

    [Test]
    public async Task DisconnectAsync_CalledTwice_IsIdempotent()
    {
        var (client, transport) = await ConnectedClientAsync();

        await client.DisconnectAsync();
        await client.DisconnectAsync();

        await Assert.That(transport.CloseCalled).IsTrue();
    }

    [Test]
    public async Task DisconnectAsync_NeverConnected_DoesNotThrow()
    {
        var client = new ClaudeSdkClient();

        await client.DisconnectAsync();
    }

    [Test]
    public async Task DisposeAsync_DisconnectsClient()
    {
        var (client, transport) = await ConnectedClientAsync();

        await client.DisposeAsync();

        await Assert.That(transport.CloseCalled).IsTrue();
    }

    // Emits a successful control_response for a captured control_request write. Round-trips through
    // JSON text before emitting: ScriptedTransport.Emit() hands the read loop the JsonObject as-is,
    // so a literal like `["totalTokens"] = 100` would stay a CLR-boxed JsonValue<int> whose
    // TryGetValue<long>() only succeeds for an exact type match. Real CLI stdout is always
    // JsonNode.Parse()'d text (JsonElement-backed), which converts between numeric types happily;
    // reparsing here matches that and avoids a test-only false negative.
    private static Task AckAsync(ScriptedTransport transport, JsonObject controlRequestWrite, JsonObject? payload = null)
    {
        var requestId = controlRequestWrite["request_id"]!.GetValue<string>();
        var envelope = new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = payload ?? new JsonObject(),
            },
        };

        transport.Emit((JsonObject)JsonNode.Parse(envelope.ToJsonString())!);
        return Task.CompletedTask;
    }

    private static JsonObject AssistantMessageJson(string text) => new()
    {
        ["type"] = "assistant",
        ["session_id"] = "session-1",
        ["message"] = new JsonObject
        {
            ["model"] = "claude-test",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        },
    };

    private static JsonObject ResultMessageJson(string sessionId = "session-1", string? terminalReason = null)
    {
        var json = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["duration_ms"] = 1,
            ["duration_api_ms"] = 1,
            ["num_turns"] = 1,
            ["session_id"] = sessionId,
        };
        if (terminalReason is not null)
        {
            json["terminal_reason"] = terminalReason;
        }

        return json;
    }

    private sealed class ThrowingConnectTransport : ITransport
    {
        public bool IsReady => false;

        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated connect failure");

        public Task WriteAsync(string data, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<JsonObject> ReadMessagesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CloseAsync() => Task.CompletedTask;

        public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
