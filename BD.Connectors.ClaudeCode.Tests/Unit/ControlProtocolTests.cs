using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Hooks;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Permissions;
using BD.Connectors.ClaudeCode.Tests.Fakes;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Behavior-level port of PY's query/tool-callback/client tests against ControlProtocol, plus a
// number of scenarios called out explicitly below.
[Property("TestKind", "Unit")]
public class ControlProtocolTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GraceWindow = TimeSpan.FromMilliseconds(150);

    private static ControlProtocol CreateProtocol(
        ScriptedTransport transport,
        CanUseToolCallback? canUseTool = null,
        IReadOnlyDictionary<string, IReadOnlyList<InternalHookMatcher>>? hooks = null,
        TimeSpan? initializeTimeout = null,
        IReadOnlyDictionary<string, ISdkMcpServer>? sdkMcpServers = null)
    {
        return new ControlProtocol(
            transport,
            canUseTool,
            hooks,
            sdkMcpServers,
            initializeTimeout ?? ShortWait,
            agents: null,
            excludeDynamicSections: null,
            systemPromptSnapshot: null,
            skills: null,
            forwardSubagentText: false,
            verbatimPrompts: false);
    }

    // Never completes/faults on its own; only resolves once the test releases it.
    private static CanUseToolCallback AllowEverything() => (_, input, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow(input));

    [Test]
    public async Task InitializeAsync_SendsHooksAgentsAndSkills_ReturnsResponse()
    {
        var transport = new ScriptedTransport { AutoRespondInitialize = true };
        var called = new List<string>();
        HookCallback callback = (_, _, _) =>
        {
            called.Add("invoked");
            return Task.FromResult<HookJsonOutput>(new SyncHookJsonOutput());
        };
        var hooks = new Dictionary<string, IReadOnlyList<InternalHookMatcher>>
        {
            ["PreToolUse"] = [new InternalHookMatcher("Bash", [callback], 30)],
        };

        using var protocol = CreateProtocol(transport, hooks: hooks);
        protocol.Start();

        var response = await protocol.InitializeAsync();

        await Assert.That(transport.Writes.Count).IsEqualTo(1);
        var written = transport.Writes[0];
        await Assert.That(written["type"]!.GetValue<string>()).IsEqualTo("control_request");
        var request = written["request"]!.AsObject();
        await Assert.That(request["subtype"]!.GetValue<string>()).IsEqualTo("initialize");

        var hooksJson = request["hooks"]!.AsObject();
        var preToolUse = hooksJson["PreToolUse"]!.AsArray();
        await Assert.That(preToolUse.Count).IsEqualTo(1);
        var matcherConfig = preToolUse[0]!.AsObject();
        await Assert.That(matcherConfig["matcher"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(matcherConfig["hookCallbackIds"]!.AsArray()[0]!.GetValue<string>()).IsEqualTo("hook_0");
        await Assert.That(matcherConfig["timeout"]!.GetValue<double>()).IsEqualTo(30);

        await Assert.That(response["commands"]!.AsArray()).IsNotNull();
        await Assert.That(protocol.InitializationResult).IsNotNull();
    }

    [Test]
    public async Task InitializeAsync_NoHooks_SendsNullHooksKey()
    {
        var transport = new ScriptedTransport { AutoRespondInitialize = true };
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        await protocol.InitializeAsync();

        var request = transport.Writes[0]["request"]!.AsObject();
        await Assert.That(request.ContainsKey("hooks")).IsTrue();
        await Assert.That(request["hooks"]).IsNull();
    }

    [Test]
    public async Task SendControlRequestAsync_TimesOut_ThrowsClaudeSdkExceptionNamingSubtype()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);

        var exception = await Assert.ThrowsAsync<ClaudeSdkException>(() =>
            protocol.SendControlRequestAsync(new JsonObject { ["subtype"] = "mcp_status" }, TimeSpan.FromMilliseconds(50)));

        await Assert.That(exception!.Message).IsEqualTo("Control request timeout: mcp_status");
    }

    [Test]
    public async Task ReadLoop_ControlResponseError_FailsThePendingRequest()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        var task = protocol.GetMcpStatusAsync();
        var written = await transport.WaitForWriteAsync(w => w["type"]?.GetValue<string>() == "control_request", ShortWait);
        var requestId = written["request_id"]!.GetValue<string>();

        transport.Emit(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = "boom" },
        });

        var exception = await Assert.ThrowsAsync<ClaudeSdkException>(() => task);
        await Assert.That(exception!.Message).IsEqualTo("boom");
    }

    // Two concurrent can_use_tool requests, the first slow;
    // responses must each carry the right request_id and the fast one must not wait on the slow one.
    [Test]
    public async Task CanUseTool_TwoConcurrentRequests_EachRespondsWithItsOwnRequestId()
    {
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRespondedFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        CanUseToolCallback callback = async (toolName, input, _) =>
        {
            if (toolName == "SlowTool")
            {
                await slowGate.Task;
                return new PermissionResultAllow(input);
            }

            fastRespondedFirst.TrySetResult(true);
            return new PermissionResultAllow(input);
        };

        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, canUseTool: callback);
        protocol.Start();

        transport.Emit(CanUseToolRequest("req_slow", "SlowTool"));
        transport.Emit(CanUseToolRequest("req_fast", "FastTool"));

        var fastResponse = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_fast"), ShortWait);
        await Assert.That(fastResponse["response"]!["response"]!["behavior"]!.GetValue<string>()).IsEqualTo("allow");
        await Assert.That(await fastRespondedFirst.Task).IsTrue();

        // The slow request must still be unanswered while only the fast one has responded.
        await Assert.That(transport.Writes.Any(w => IsControlResponseFor(w, "req_slow"))).IsFalse();

        slowGate.SetResult();
        var slowResponse = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_slow"), ShortWait);
        await Assert.That(slowResponse["response"]!["request_id"]!.GetValue<string>()).IsEqualTo("req_slow");
    }

    // A control_cancel_request during a slow callback cancels its
    // token and no response is ever written for that request.
    [Test]
    public async Task ControlCancelRequest_DuringSlowCallback_CancelsTokenAndWritesNoResponse()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        CanUseToolCallback callback = async (_, input, context) =>
        {
            using var registration = context.CancellationToken.Register(() => observedCancellation.TrySetResult(true));
            callbackStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return new PermissionResultAllow(input);
        };

        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, canUseTool: callback);
        protocol.Start();

        transport.Emit(CanUseToolRequest("req_cancel", "SlowTool"));
        await callbackStarted.Task;

        transport.Emit(new JsonObject { ["type"] = "control_cancel_request", ["request_id"] = "req_cancel" });

        await Assert.That(await observedCancellation.Task).IsTrue();
        await Task.Delay(GraceWindow);
        await Assert.That(transport.Writes.Any(w => IsControlResponseFor(w, "req_cancel"))).IsFalse();
    }

    // A result while a local_agent task is in flight must not
    // close stdin; a second result after the task's task_notification does.
    [Test]
    public async Task Result_WithDeferringTaskInFlight_KeepsStdinOpenUntilTaskSettles()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, canUseTool: AllowEverything());
        protocol.Start();
        _ = protocol.SpawnTask(_ => protocol.WaitForResultAndEndInputAsync());

        transport.Emit(SystemMessage("task_started", new JsonObject { ["task_id"] = "t1", ["task_type"] = "local_agent" }));
        transport.Emit(ResultMessage(isError: false));

        await Task.Delay(GraceWindow);
        await Assert.That(transport.EndInputCalled).IsFalse();

        transport.Emit(SystemMessage("task_notification", new JsonObject { ["task_id"] = "t1" }));
        transport.Emit(ResultMessage(isError: false));

        await WaitForEndInput(transport);
    }

    // A task_started with a non-deferring task_type closes stdin
    // at the very first result.
    [Test]
    public async Task Result_WithNonDeferringTaskInFlight_ClosesStdinAtFirstResult()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, canUseTool: AllowEverything());
        protocol.Start();
        _ = protocol.SpawnTask(_ => protocol.WaitForResultAndEndInputAsync());

        transport.Emit(SystemMessage("task_started", new JsonObject { ["task_id"] = "t2", ["task_type"] = "local_bash" }));
        transport.Emit(ResultMessage(isError: false));

        await WaitForEndInput(transport);
    }

    // A task_updated patch with a terminal status clears the task.
    [Test]
    public async Task TaskUpdated_WithTerminalStatus_ClearsInFlightTask()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, canUseTool: AllowEverything());
        protocol.Start();
        _ = protocol.SpawnTask(_ => protocol.WaitForResultAndEndInputAsync());

        transport.Emit(SystemMessage("task_started", new JsonObject { ["task_id"] = "t3", ["task_type"] = "local_agent" }));
        transport.Emit(SystemMessage("task_updated", new JsonObject { ["task_id"] = "t3", ["patch"] = new JsonObject { ["status"] = "killed" } }));
        transport.Emit(ResultMessage(isError: false));

        await WaitForEndInput(transport);
    }

    // An error result followed by a non-zero process exit
    // surfaces as a ResultException carrying the result's own text, not a bare exit-code error.
    [Test]
    public async Task ErrorResultFollowedByProcessExit_SurfacesAsResultException()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        var errorResult = ResultMessage(isError: true, result: "API Error: overloaded");
        transport.Emit(errorResult);
        transport.Complete(new ProcessException("Command failed with exit code 1", 1, "stderr"));

        var messages = new List<JsonObject>();
        var exception = await Assert.ThrowsAsync<ResultException>(async () =>
        {
            await foreach (var message in protocol.ReceiveMessagesAsync())
            {
                messages.Add(message);
            }
        });

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(exception!.ExitCode).IsEqualTo(1);
        await Assert.That(exception.Message).Contains("API Error: overloaded");
    }

    // A transport failure during initialize fails it right away,
    // not after the (here, deliberately long) initialize timeout.
    [Test]
    public async Task InitializeAsync_TransportFailsMidHandshake_FailsImmediatelyNotAfterTimeout()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, initializeTimeout: TimeSpan.FromSeconds(30));
        protocol.Start();

        var initializeTask = protocol.InitializeAsync();
        transport.Complete(new InvalidOperationException("boom"));

        var completed = await Task.WhenAny(initializeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(completed, initializeTask)).IsTrue();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => initializeTask);
        await Assert.That(exception!.Message).IsEqualTo("boom");
    }

    // Cancelling the consumer's enumeration must let
    // ReceiveMessagesAsync surface an OperationCanceledException rather than hang.
    [Test]
    public async Task ReceiveMessagesAsync_ConsumerCancels_ThrowsOperationCanceled()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        using var cts = new CancellationTokenSource();
        var enumerator = protocol.ReceiveMessagesAsync(cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await moveNext);
    }

    // With no callbacks/hooks/MCP configured, stdin closes at
    // once -- WaitForResultAndEndInputAsync never has to wait on a result to begin with.
    [Test]
    public async Task WaitForResultAndEndInputAsync_NoBidirectionalNeeds_ClosesStdinImmediately()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        await protocol.WaitForResultAndEndInputAsync();

        await Assert.That(transport.EndInputCalled).IsTrue();
    }

    [Test]
    public async Task HandleControlRequest_UnknownHookCallbackId_WritesErrorResponse()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        transport.Emit(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = "req_hook",
            ["request"] = new JsonObject { ["subtype"] = "hook_callback", ["callback_id"] = "hook_missing" },
        });

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_hook"), ShortWait);
        await Assert.That(written["response"]!["subtype"]!.GetValue<string>()).IsEqualTo("error");
        await Assert.That(written["response"]!["error"]!.GetValue<string>()).IsEqualTo("No hook callback found for ID: hook_missing");
    }

    [Test]
    public async Task HandleControlRequest_CanUseToolWithoutCallback_WritesErrorResponse()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport); // no canUseTool configured
        protocol.Start();

        transport.Emit(CanUseToolRequest("req_no_cb", "AnyTool"));

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_no_cb"), ShortWait);
        await Assert.That(written["response"]!["subtype"]!.GetValue<string>()).IsEqualTo("error");
        await Assert.That(written["response"]!["error"]!.GetValue<string>()).IsEqualTo("canUseTool callback is not provided");
    }

    [Test]
    public async Task HandleControlRequest_DenyWithInterrupt_IncludesInterruptFlag()
    {
        CanUseToolCallback callback = (_, _, _) => Task.FromResult<PermissionResult>(new PermissionResultDeny("nope", Interrupt: true));
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, canUseTool: callback);
        protocol.Start();

        transport.Emit(CanUseToolRequest("req_deny", "AnyTool"));

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_deny"), ShortWait);
        var response = written["response"]!["response"]!.AsObject();
        await Assert.That(response["behavior"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(response["message"]!.GetValue<string>()).IsEqualTo("nope");
        await Assert.That(response["interrupt"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task HandleControlRequest_UnsupportedSubtype_WritesErrorResponse()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        transport.Emit(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = "req_unsupported",
            ["request"] = new JsonObject { ["subtype"] = "made_up" },
        });

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_unsupported"), ShortWait);
        await Assert.That(written["response"]!["error"]!.GetValue<string>()).IsEqualTo("Unsupported control request subtype: made_up");
    }

    // F5.3: _mcpBridges is populated from the constructor's sdkMcpServers parameter; routing itself
    // (HandleMcpMessageRequestAsync/HandleSdkMcpRequestAsync) was already built and tested against an
    // always-empty table in F3 (see PORTING_STATUS.md Fase 3 "Afwijkingen van PY").
    [Test]
    public async Task HandleControlRequest_McpMessage_UnknownServer_WritesServerNotFoundMcpResponse()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport); // no sdkMcpServers configured
        protocol.Start();

        transport.Emit(McpMessageRequest("req_mcp", "missing-server", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" }));

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_mcp"), ShortWait);
        var mcpResponse = written["response"]!["response"]!["mcp_response"]!.AsObject();
        await Assert.That(mcpResponse["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32601);
        await Assert.That(mcpResponse["error"]!["message"]!.GetValue<string>()).IsEqualTo("Server 'missing-server' not found");
    }

    [Test]
    public async Task HandleControlRequest_McpMessage_ConfiguredServer_RoutesThroughBridge()
    {
        var stub = new StubSdkMcpServer("calc", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject { ["tools"] = new JsonArray() } });
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, sdkMcpServers: new Dictionary<string, ISdkMcpServer> { ["calc"] = stub });
        protocol.Start();

        var mcpMessage = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" };
        transport.Emit(McpMessageRequest("req_mcp", "calc", mcpMessage));

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_mcp"), ShortWait);
        var mcpResponse = written["response"]!["response"]!["mcp_response"]!.AsObject();
        await Assert.That(mcpResponse["result"]!["tools"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(stub.LastMessage!["method"]!.GetValue<string>()).IsEqualTo("tools/list");
    }

    [Test]
    public async Task HandleControlRequest_McpMessage_NotificationResult_AcksWithEmptyResult()
    {
        var stub = new StubSdkMcpServer("calc", response: null);
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport, sdkMcpServers: new Dictionary<string, ISdkMcpServer> { ["calc"] = stub });
        protocol.Start();

        transport.Emit(McpMessageRequest("req_mcp", "calc", new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_mcp"), ShortWait);
        var mcpResponse = written["response"]!["response"]!["mcp_response"]!.AsObject();
        await Assert.That(mcpResponse["jsonrpc"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(mcpResponse["result"]!.AsObject().Count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleControlRequest_McpMessage_MissingServerNameOrMessage_WritesErrorResponse()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        transport.Emit(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = "req_mcp_bad",
            ["request"] = new JsonObject { ["subtype"] = "mcp_message" },
        });

        var written = await transport.WaitForWriteAsync(w => IsControlResponseFor(w, "req_mcp_bad"), ShortWait);
        await Assert.That(written["response"]!["subtype"]!.GetValue<string>()).IsEqualTo("error");
        await Assert.That(written["response"]!["error"]!.GetValue<string>()).IsEqualTo("Missing server_name or message for MCP request");
    }

    [Test]
    public async Task StampUserMessage_VerbatimPrompts_SetsClientComposed()
    {
        var message = new JsonObject { ["type"] = "user" };

        var stamped = ControlProtocol.StampUserMessage(message, verbatimPrompts: true);

        await Assert.That(stamped["client_composed"]!.GetValue<bool>()).IsTrue();
        await Assert.That(message.ContainsKey("client_composed")).IsFalse();
    }

    [Test]
    public async Task StampUserMessage_NotVerbatim_ReturnsSameInstance()
    {
        var message = new JsonObject { ["type"] = "user" };

        var stamped = ControlProtocol.StampUserMessage(message, verbatimPrompts: false);

        await Assert.That(ReferenceEquals(stamped, message)).IsTrue();
    }

    [Test]
    [Arguments("""{"errors":["first","second"]}""", "first; second")]
    [Arguments("""{"result":" API Error: bad "}""", "API Error: bad")]
    [Arguments("""{"subtype":"error_max_turns"}""", "error_max_turns")]
    [Arguments("""{"subtype":"success","api_error_status":529}""", "API error (HTTP 529)")]
    [Arguments("""{"subtype":"success"}""", "unknown error")]
    public async Task ErrorResultText_PicksMostInformativeField(string json, string expected)
    {
        var message = (JsonObject)JsonNode.Parse(json)!;

        await Assert.That(ControlProtocol.ErrorResultText(message)).IsEqualTo(expected);
    }

    [Test]
    public async Task CloseAsync_ClosesTransportAndIsIdempotent()
    {
        var transport = new ScriptedTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Start();

        await protocol.CloseAsync();
        await protocol.CloseAsync();

        await Assert.That(transport.CloseCalled).IsTrue();
    }

    private static async Task WaitForEndInput(ScriptedTransport transport)
    {
        var completed = await Task.WhenAny(transport.EndInputSignal, Task.Delay(ShortWait));
        await Assert.That(ReferenceEquals(completed, transport.EndInputSignal)).IsTrue();
    }

    private static bool IsControlResponseFor(JsonObject written, string requestId) =>
        written["type"]?.GetValue<string>() == "control_response" && written["response"]?["request_id"]?.GetValue<string>() == requestId;

    private static JsonObject CanUseToolRequest(string requestId, string toolName) => new()
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = new JsonObject
        {
            ["subtype"] = "can_use_tool",
            ["tool_name"] = toolName,
            ["input"] = new JsonObject(),
        },
    };

    private static JsonObject McpMessageRequest(string requestId, string serverName, JsonObject message) => new()
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = new JsonObject
        {
            ["subtype"] = "mcp_message",
            ["server_name"] = serverName,
            ["message"] = message,
        },
    };

    private sealed class StubSdkMcpServer(string name, JsonObject? response) : ISdkMcpServer
    {
        public string Name => name;

        public JsonObject? LastMessage { get; private set; }

        public Task<JsonObject?> HandleAsync(JsonObject jsonRpcMessage, CancellationToken cancellationToken)
        {
            LastMessage = jsonRpcMessage;
            return Task.FromResult(response);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static JsonObject SystemMessage(string subtype, JsonObject extra)
    {
        var message = new JsonObject { ["type"] = "system", ["subtype"] = subtype };
        foreach (var (key, value) in extra)
        {
            message[key] = value?.DeepClone();
        }

        return message;
    }

    private static JsonObject ResultMessage(bool isError, string? result = null)
    {
        var message = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = isError,
            ["duration_ms"] = 1,
            ["duration_api_ms"] = 1,
            ["num_turns"] = 1,
            ["session_id"] = "session-1",
        };
        if (result is not null)
        {
            message["result"] = result;
        }

        return message;
    }
}
