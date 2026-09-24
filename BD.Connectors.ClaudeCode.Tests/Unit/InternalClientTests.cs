using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Tests.Fakes;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Behavior-level port of the relevant parts of PY's client/integration tests against
// InternalClient.ProcessQueryAsync, which wires ControlProtocol into the query pipeline.
[Property("TestKind", "Unit")]
public class InternalClientTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ProcessQueryAsync_StringPrompt_SendsInitializeThenUserMessage_YieldsParsedMessagesAndCloses()
    {
        var transport = new ScriptedTransport { AutoRespondInitialize = true };

        var pump = Task.Run(async () =>
        {
            var userWrite = await transport.WaitForWriteAsync(w => w["type"]?.GetValue<string>() == "user", ShortWait);
            await Assert.That(userWrite["session_id"]!.GetValue<string>()).IsEqualTo(string.Empty);
            await Assert.That(userWrite["message"]!["role"]!.GetValue<string>()).IsEqualTo("user");
            await Assert.That(userWrite["message"]!["content"]!.GetValue<string>()).IsEqualTo("Hello");

            transport.Emit(AssistantMessageJson("Hi there"));
            transport.Emit(ResultMessageJson());
            transport.Complete();
        });

        var messages = new List<Message>();
        await foreach (var message in InternalClient.ProcessQueryAsync("Hello", new ClaudeAgentOptions(), transport))
        {
            messages.Add(message);
        }

        await pump;

        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages[0]).IsTypeOf<AssistantMessage>();
        await Assert.That(messages[1]).IsTypeOf<ResultMessage>();
        await Assert.That(transport.CloseCalled).IsTrue();
    }

    [Test]
    public async Task ProcessQueryAsync_StreamPrompt_ForwardsEachMessage()
    {
        var transport = new ScriptedTransport { AutoRespondInitialize = true };

        static async IAsyncEnumerable<JsonObject> Prompts()
        {
            yield return new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "one" } };
            await Task.Yield();
            yield return new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "two" } };
        }

        var pump = Task.Run(async () =>
        {
            await transport.WaitForWriteAsync(w => w["message"]?["content"]?.GetValue<string>() == "two", ShortWait);
            transport.Emit(ResultMessageJson());
            transport.Complete();
        });

        var messages = new List<Message>();
        await foreach (var message in InternalClient.ProcessQueryAsync(Prompts(), new ClaudeAgentOptions(), transport))
        {
            messages.Add(message);
        }

        await pump;

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0]).IsTypeOf<ResultMessage>();
        await Assert.That(transport.Writes.Count(w => w["type"]?.GetValue<string>() == "user")).IsEqualTo(2);
    }

    // Same scenario as ControlProtocolTests' cancellation test, but at the InternalClient level:
    // cancelling the consumer's enumeration must still run the pipeline's finally block, closing the
    // protocol and transport.
    [Test]
    public async Task ProcessQueryAsync_ConsumerCancels_ClosesTransport()
    {
        var transport = new ScriptedTransport { AutoRespondInitialize = true };
        using var cts = new CancellationTokenSource();

        var enumerator = InternalClient.ProcessQueryAsync("Hello", new ClaudeAgentOptions(), transport, cts.Token).GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        await transport.WaitForWriteAsync(w => w["type"]?.GetValue<string>() == "user", ShortWait);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await moveNextTask);

        var deadline = DateTime.UtcNow.Add(ShortWait);
        while (!transport.CloseCalled && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await Assert.That(transport.CloseCalled).IsTrue();
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

    private static JsonObject ResultMessageJson() => new()
    {
        ["type"] = "result",
        ["subtype"] = "success",
        ["is_error"] = false,
        ["duration_ms"] = 1,
        ["duration_api_ms"] = 1,
        ["num_turns"] = 1,
        ["session_id"] = "session-1",
    };
}
