using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Hooks;
using BD.Connectors.ClaudeCode.Logging;
using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Permissions;
using BD.Connectors.ClaudeCode.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Internal;

// Handles the bidirectional control protocol on top of ITransport: control request/response
// routing, hook callbacks, tool permission callbacks, message streaming and the initialize
// handshake. Literal port of PY's `Query` class.
//
// `is_streaming_mode` from PY has no field here: this SDK always runs the control protocol in
// streaming mode (matching the TypeScript SDK), so every check PY makes against it is always true.
internal sealed class ControlProtocol : IDisposable
{
    // Task types whose completion runs a follow-up turn and therefore may still need the control
    // channel after the turn's result frame; see TrackTaskLifecycle.
    internal static readonly FrozenSet<string> _deferringTaskTypes = FrozenSet.ToFrozenSet(["local_agent", "local_workflow"]);

    private static readonly TimeSpan _defaultControlTimeout = TimeSpan.FromSeconds(60);

    private readonly ITransport _transport;
    private readonly CanUseToolCallback? _canUseTool;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<InternalHookMatcher>> _hooks;
    private readonly Dictionary<string, SdkMcpBridge> _mcpBridges = [];
    private readonly TimeSpan _initializeTimeout;
    private readonly JsonObject? _agents;
    private readonly bool? _excludeDynamicSections;
    private readonly bool? _systemPromptSnapshot;
    private readonly SkillsConfig? _skills;
    private readonly bool _forwardSubagentText;
    private readonly bool _verbatimPrompts;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingControlResponses = new();
    private readonly ConcurrentDictionary<string, HookCallback> _hookCallbacks = new();
    private int _nextCallbackId;
    private int _requestCounter;

    private readonly Channel<Frame> _messages = Channel.CreateBounded<Frame>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readTask;
    private readonly ConcurrentDictionary<int, Task> _childTasks = new();
    private int _childTaskIdCounter;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflightRequests = new();

    // Set when a run-ending result arrives (a result frame with no tasks in flight); see #1088.
    private readonly TaskCompletionSource _firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Task IDs of started-but-not-finished tasks; read loop only, no lock needed (single reader).
    private readonly HashSet<string> _inflightTasks = [];

    // Set to the result payload when the most recent message is a result with is_error=True; read
    // loop only. See TrackTaskLifecycle/ErrorResultText.
    private JsonObject? _lastErrorResult;

    private volatile bool _closed;

    // SessionStore mirroring (set via SetTranscriptMirrorBatcher); F7.
    private TranscriptMirrorBatcher? _mirrorBatcher;

    public JsonObject? InitializationResult { get; private set; }

    public ControlProtocol(
        ITransport transport,
        CanUseToolCallback? canUseTool,
        IReadOnlyDictionary<string, IReadOnlyList<InternalHookMatcher>>? hooks,
        IReadOnlyDictionary<string, ISdkMcpServer>? sdkMcpServers,
        TimeSpan initializeTimeout,
        JsonObject? agents,
        bool? excludeDynamicSections,
        bool? systemPromptSnapshot,
        SkillsConfig? skills,
        bool forwardSubagentText,
        bool verbatimPrompts,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _canUseTool = canUseTool;
        _hooks = hooks ?? new Dictionary<string, IReadOnlyList<InternalHookMatcher>>();
        _initializeTimeout = initializeTimeout;
        _agents = agents;
        _excludeDynamicSections = excludeDynamicSections;
        _systemPromptSnapshot = systemPromptSnapshot;
        _skills = skills;
        _forwardSubagentText = forwardSubagentText;
        _verbatimPrompts = verbatimPrompts;
        _logger = logger ?? NullLogger.Instance;

        if (sdkMcpServers is not null)
        {
            foreach (var (name, server) in sdkMcpServers)
            {
                _mcpBridges[name] = new SdkMcpBridge(name, server, _logger);
            }
        }
    }

    private readonly record struct Frame(JsonObject? Message, Exception? Error, bool IsEnd);

    public void SetTranscriptMirrorBatcher(TranscriptMirrorBatcher batcher) => _mirrorBatcher = batcher;

    // PY's `report_mirror_error`. Called from the batcher's on_error; non-blocking -- if the
    // message buffer is full the error is logged and dropped rather than back-pressuring the read
    // loop.
    public void ReportMirrorError(JsonObject? key, string error)
    {
        var message = new JsonObject
        {
            [WireConstants.Keys.Type] = WireConstants.MessageTypes.System,
            [WireConstants.Keys.Subtype] = WireConstants.SystemSubtypes.MirrorError,
            ["error"] = error,
            ["key"] = key?.DeepClone(),
            [WireConstants.Keys.Uuid] = Guid.NewGuid().ToString(),
            [WireConstants.Keys.SessionId] = key is not null ? JsonHelpers.GetString(key, "session_id") ?? string.Empty : string.Empty,
        };

        if (!_messages.Writer.TryWrite(new Frame(message, null, false)))
        {
            SdkLog.DroppingMirrorErrorMessage(_logger);
        }
    }

    // PY's `initialize`.
    public async Task<JsonObject> InitializeAsync(CancellationToken cancellationToken = default)
    {
        JsonObject? hooksConfig = null;
        if (_hooks.Count > 0)
        {
            var config = new JsonObject();
            foreach (var (eventName, matchers) in _hooks)
            {
                if (matchers.Count == 0)
                {
                    continue;
                }

                var matcherConfigs = new List<JsonNode?>();
                foreach (var matcher in matchers)
                {
                    var callbackIds = new List<string>();
                    foreach (var callback in matcher.Callbacks)
                    {
                        var callbackId = $"hook_{_nextCallbackId++}";
                        _hookCallbacks[callbackId] = callback;
                        callbackIds.Add(callbackId);
                    }

                    var matcherConfig = new JsonObject { ["matcher"] = matcher.Matcher };
                    JsonHelpers.SetStringArray(matcherConfig, "hookCallbackIds", callbackIds);
                    if (matcher.TimeoutSeconds is { } timeoutSeconds)
                    {
                        matcherConfig["timeout"] = timeoutSeconds;
                    }

                    matcherConfigs.Add(matcherConfig);
                }

                config[eventName] = new JsonArray([.. matcherConfigs]);
            }

            hooksConfig = config.Count > 0 ? config : null;
        }

        var request = new JsonObject
        {
            ["subtype"] = WireConstants.ControlSubtypes.Initialize,
            ["hooks"] = hooksConfig,
        };

        if (_agents is not null)
        {
            request["agents"] = _agents.DeepClone();
        }

        if (_excludeDynamicSections is { } excludeDynamicSections)
        {
            request["excludeDynamicSections"] = excludeDynamicSections;
        }

        if (_systemPromptSnapshot is { } systemPromptSnapshot)
        {
            request["systemPromptSnapshot"] = systemPromptSnapshot;
        }

        // 'all' and omitted are equivalent at the wire level (no filter); only send an explicit list.
        if (_skills is SkillsConfig.Named namedSkills)
        {
            JsonHelpers.SetStringArray(request, "skills", namedSkills.Skills);
        }

        if (_forwardSubagentText)
        {
            request["forwardSubagentText"] = true;
        }

        var response = await SendControlRequestAsync(request, _initializeTimeout, cancellationToken).ConfigureAwait(false);
        InitializationResult = response;
        return response;
    }

    // PY's `start`.
    public void Start() => _readTask ??= Task.Run(ReadMessagesLoopAsync);

    // PY's `spawn_task`. Spawns a child task cancelled (via the shared lifetime token) on Close().
    public Task SpawnTask(Func<CancellationToken, Task> work)
    {
        var id = Interlocked.Increment(ref _childTaskIdCounter);
        var task = RunSpawnedAsync(work);
        _childTasks[id] = task;
        _ = task.ContinueWith(completedTask => _childTasks.TryRemove(id, out _), TaskScheduler.Default);
        return task;
    }

    private async Task RunSpawnedAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            await work(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            SdkLog.UnhandledBackgroundTaskException(_logger, e);
        }
    }

    // PY's `_spawn_control_request_handler`. Spawned separately from SpawnTask: each handler gets
    // its own cancellation source (linked to the shared lifetime) so a control_cancel_request can
    // cancel just this one.
    private void SpawnControlRequestHandler(JsonObject request)
    {
        var requestId = JsonHelpers.GetString(request, "request_id");
        if (requestId is null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _inflightRequests[requestId] = cts;

        var id = Interlocked.Increment(ref _childTaskIdCounter);
        var task = RunControlRequestHandlerAsync(request, cts);
        _childTasks[id] = task;
        _ = task.ContinueWith(
            completedTask =>
            {
                _childTasks.TryRemove(id, out _);
                _inflightRequests.TryRemove(requestId, out _);
                cts.Dispose();
            },
            TaskScheduler.Default);
    }

    private async Task RunControlRequestHandlerAsync(JsonObject request, CancellationTokenSource cts)
    {
        try
        {
            await HandleControlRequestAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            SdkLog.UnhandledBackgroundTaskException(_logger, e);
        }
    }

    // PY's `_read_messages`.
    private async Task ReadMessagesLoopAsync()
    {
        try
        {
            await foreach (var message in _transport.ReadMessagesAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_closed)
                {
                    break;
                }

                var type = JsonHelpers.GetString(message, WireConstants.Keys.Type);

                if (type == WireConstants.MessageTypes.ControlResponse)
                {
                    var response = JsonHelpers.GetObject(message, "response") ?? [];
                    var requestId = JsonHelpers.GetString(response, "request_id");
                    if (requestId is not null && _pendingControlResponses.TryGetValue(requestId, out var tcs))
                    {
                        if (JsonHelpers.GetString(response, "subtype") == WireConstants.ControlSubtypes.Error)
                        {
                            tcs.TrySetException(new ClaudeSdkException(JsonHelpers.GetString(response, "error") ?? "Unknown error"));
                        }
                        else
                        {
                            tcs.TrySetResult(response);
                        }
                    }

                    continue;
                }

                if (type == WireConstants.MessageTypes.ControlRequest)
                {
                    if (!_closed)
                    {
                        SpawnControlRequestHandler(message);
                    }

                    continue;
                }

                if (type == WireConstants.MessageTypes.ControlCancelRequest)
                {
                    var cancelId = JsonHelpers.GetString(message, "request_id");
                    if (cancelId is not null && _inflightRequests.TryRemove(cancelId, out var cts))
                    {
                        cts.Cancel();
                    }

                    continue;
                }

                if (type == WireConstants.MessageTypes.TranscriptMirror)
                {
                    _mirrorBatcher?.Enqueue(
                        JsonHelpers.GetRequiredString(message, "filePath"),
                        JsonHelpers.GetArray(message, "entries") ?? []);
                    continue;
                }

                // Track task lifecycle frames so results can tell "one turn ended" apart from "the
                // run is done" (see #1088).
                if (type == WireConstants.MessageTypes.System)
                {
                    TrackTaskLifecycle(message);
                }

                if (type == WireConstants.MessageTypes.Result)
                {
                    if (_mirrorBatcher is not null)
                    {
                        await _mirrorBatcher.FlushAsync().ConfigureAwait(false);
                    }

                    if (_inflightTasks.Count > 0)
                    {
                        SdkLog.ResultReceivedWithTasksInFlight(_logger, _inflightTasks.Count);
                    }
                    else
                    {
                        _firstResult.TrySetResult();
                    }

                    _lastErrorResult = JsonHelpers.GetBool(message, WireConstants.Keys.IsError) == true ? message : null;
                }
                else if (!(type == WireConstants.MessageTypes.System
                    && JsonHelpers.GetString(message, WireConstants.Keys.Subtype) == WireConstants.SystemSubtypes.SessionStateChanged))
                {
                    // Anything other than the post-turn session_state_changed marker means the
                    // conversation moved on; a ProcessException now is a fresh crash, not the
                    // expected exit from a prior error result.
                    _lastErrorResult = null;
                }

                await _messages.Writer.WriteAsync(new Frame(message, null, false), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected: Close() cancelled the lifetime token.
        }
        catch (Exception e)
        {
            var pending = e;
            if (e is ProcessException pe && _lastErrorResult is not null)
            {
                var errorText = $"Claude Code returned an error result: {ErrorResultText(_lastErrorResult)}";
                pending = new ResultException(errorText, _lastErrorResult, pe.ExitCode, pe);
                SdkLog.ReplacingProcessErrorWithResultError(_logger, pe.ExitCode);
            }
            else
            {
                SdkLog.FatalErrorInMessageReader(_logger, e);
            }

            // Signal all pending control requests so they fail fast instead of timing out.
            // TrySetException is a no-op on an already-completed TCS, matching PY's
            // "if request_id not in pending_control_results" guard.
            foreach (var tcs in _pendingControlResponses.Values)
            {
                tcs.TrySetException(pending);
            }

            await _messages.Writer.WriteAsync(new Frame(null, pending, false), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (_mirrorBatcher is not null)
            {
                await _mirrorBatcher.FlushAsync().ConfigureAwait(false);
            }

            // Unblock any waiters (e.g. the string-prompt path) so they don't stall on early exit.
            _firstResult.TrySetResult();

            _messages.Writer.TryWrite(new Frame(null, null, true));
            _messages.Writer.TryComplete();
        }
    }

    // PY's `_handle_control_request`.
    private async Task HandleControlRequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var requestId = JsonHelpers.GetRequiredString(request, "request_id");
        var requestData = JsonHelpers.GetObject(request, "request") ?? [];
        var subtype = JsonHelpers.GetString(requestData, "subtype");

        try
        {
            var responseData = subtype switch
            {
                WireConstants.ControlSubtypes.CanUseTool => await HandleCanUseToolAsync(requestData, cancellationToken).ConfigureAwait(false),
                WireConstants.ControlSubtypes.HookCallback => await HandleHookCallbackAsync(requestData, cancellationToken).ConfigureAwait(false),
                WireConstants.ControlSubtypes.McpMessage => await HandleMcpMessageRequestAsync(requestData).ConfigureAwait(false),
                _ => throw new ClaudeSdkException($"Unsupported control request subtype: {subtype}"),
            };

            var successResponse = new JsonObject
            {
                [WireConstants.Keys.Type] = WireConstants.MessageTypes.ControlResponse,
                ["response"] = new JsonObject
                {
                    ["subtype"] = WireConstants.ControlSubtypes.Success,
                    ["request_id"] = requestId,
                    ["response"] = responseData,
                },
            };
            await _transport.WriteAsync(successResponse.ToJsonString() + "\n", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled via control_cancel_request; the CLI has already abandoned this request.
            throw;
        }
        catch (Exception e)
        {
            var errorResponse = new JsonObject
            {
                [WireConstants.Keys.Type] = WireConstants.MessageTypes.ControlResponse,
                ["response"] = new JsonObject
                {
                    ["subtype"] = WireConstants.ControlSubtypes.Error,
                    ["request_id"] = requestId,
                    ["error"] = e.Message,
                },
            };

            try
            {
                await _transport.WriteAsync(errorResponse.ToJsonString() + "\n", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception writeError)
            {
                SdkLog.ControlResponseWriteFailed(_logger, writeError);
            }
        }
    }

    private async Task<JsonObject> HandleCanUseToolAsync(JsonObject requestData, CancellationToken cancellationToken)
    {
        var originalInput = JsonHelpers.GetObject(requestData, "input") ?? [];
        if (_canUseTool is null)
        {
            throw new ClaudeSdkException("canUseTool callback is not provided");
        }

        var suggestions = new List<PermissionUpdate>();
        if (JsonHelpers.GetArray(requestData, "permission_suggestions") is { } rawSuggestions)
        {
            foreach (var node in rawSuggestions)
            {
                if (node is JsonObject suggestion)
                {
                    suggestions.Add(PermissionUpdate.FromJson(suggestion));
                }
            }
        }

        var context = new ToolPermissionContext
        {
            Suggestions = suggestions,
            ToolUseId = JsonHelpers.GetString(requestData, "tool_use_id"),
            AgentId = JsonHelpers.GetString(requestData, "agent_id"),
            BlockedPath = JsonHelpers.GetString(requestData, "blocked_path"),
            DecisionReason = JsonHelpers.GetString(requestData, "decision_reason"),
            Title = JsonHelpers.GetString(requestData, "title"),
            DisplayName = JsonHelpers.GetString(requestData, "display_name"),
            Description = JsonHelpers.GetString(requestData, "description"),
            CancellationToken = cancellationToken,
        };

        var toolName = JsonHelpers.GetRequiredString(requestData, "tool_name");
        var response = await _canUseTool(toolName, originalInput, context).ConfigureAwait(false);

        switch (response)
        {
            case PermissionResultAllow allow:
            {
                var result = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = (allow.UpdatedInput ?? originalInput).DeepClone() };
                if (allow.UpdatedPermissions is not null)
                {
                    result["updatedPermissions"] = new JsonArray([.. allow.UpdatedPermissions.Select(p => (JsonNode?)p.ToJson())]);
                }

                return result;
            }

            case PermissionResultDeny deny:
            {
                var result = new JsonObject { ["behavior"] = "deny", ["message"] = deny.Message };
                if (deny.Interrupt)
                {
                    result["interrupt"] = true;
                }

                return result;
            }

            default:
                throw new ClaudeSdkException(
                    "Tool permission callback must return PermissionResult (PermissionResultAllow or PermissionResultDeny), "
                    + $"got {response?.GetType().Name ?? "null"}");
        }
    }

    private async Task<JsonObject> HandleHookCallbackAsync(JsonObject requestData, CancellationToken cancellationToken)
    {
        var callbackId = JsonHelpers.GetRequiredString(requestData, "callback_id");
        if (!_hookCallbacks.TryGetValue(callbackId, out var callback))
        {
            throw new ClaudeSdkException($"No hook callback found for ID: {callbackId}");
        }

        var input = HookInput.FromJson(JsonHelpers.GetObject(requestData, "input") ?? []);
        var toolUseId = JsonHelpers.GetString(requestData, "tool_use_id");
        var output = await callback(input, toolUseId, new HookContext { CancellationToken = cancellationToken }).ConfigureAwait(false);
        return output.ToJson();
    }

    private async Task<JsonObject> HandleMcpMessageRequestAsync(JsonObject requestData)
    {
        var serverName = JsonHelpers.GetString(requestData, "server_name");
        var mcpMessage = JsonHelpers.GetObject(requestData, "message");
        if (string.IsNullOrEmpty(serverName) || mcpMessage is null)
        {
            throw new ClaudeSdkException("Missing server_name or message for MCP request");
        }

        var mcpResponse = await HandleSdkMcpRequestAsync(serverName, mcpMessage).ConfigureAwait(false)
            ?? new JsonObject { ["jsonrpc"] = "2.0", ["result"] = new JsonObject() };

        return new JsonObject { ["mcp_response"] = mcpResponse };
    }

    // PY's `_handle_sdk_mcp_request`.
    private async Task<JsonObject?> HandleSdkMcpRequestAsync(string serverName, JsonObject message)
    {
        if (!_mcpBridges.TryGetValue(serverName, out var bridge))
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonHelpers.GetNode(message, "id")?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Server '{serverName}' not found" },
            };
        }

        try
        {
            return await bridge.HandleAsync(message).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonHelpers.GetNode(message, "id")?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32603, ["message"] = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message },
            };
        }
    }

    // PY's `_send_control_request`.
    public async Task<JsonObject> SendControlRequestAsync(JsonObject request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var requestId = $"req_{Interlocked.Increment(ref _requestCounter)}_{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControlResponses[requestId] = tcs;

        var controlRequest = new JsonObject
        {
            [WireConstants.Keys.Type] = WireConstants.MessageTypes.ControlRequest,
            ["request_id"] = requestId,
            ["request"] = request,
        };

        await _transport.WriteAsync(controlRequest.ToJsonString() + "\n", cancellationToken).ConfigureAwait(false);

        try
        {
            JsonObject fullResponse;
            try
            {
                fullResponse = await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pendingControlResponses.TryRemove(requestId, out _);
            }

            return JsonHelpers.GetObject(fullResponse, "response") ?? [];
        }
        catch (TimeoutException)
        {
            throw new ClaudeSdkException($"Control request timeout: {JsonHelpers.GetString(request, "subtype")}");
        }
    }

    public Task<JsonObject> GetMcpStatusAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.McpStatus }, _defaultControlTimeout, cancellationToken);

    public Task<JsonObject> GetContextUsageAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.GetContextUsage }, _defaultControlTimeout, cancellationToken);

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.Interrupt }, _defaultControlTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetPermissionModeAsync(PermissionMode mode, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.SetPermissionMode, ["mode"] = mode.ToWireValue() };
        await SendControlRequestAsync(request, _defaultControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.SetModel, ["model"] = model };
        await SendControlRequestAsync(request, _defaultControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.RewindFiles, ["user_message_id"] = userMessageId };
        await SendControlRequestAsync(request, _defaultControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconnectMcpServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.McpReconnect, ["serverName"] = serverName };
        await SendControlRequestAsync(request, _defaultControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task ToggleMcpServerAsync(string serverName, bool enabled, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.McpToggle, ["serverName"] = serverName, ["enabled"] = enabled };
        await SendControlRequestAsync(request, _defaultControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["subtype"] = WireConstants.ControlSubtypes.StopTask, ["task_id"] = taskId };
        await SendControlRequestAsync(request, _defaultControlTimeout, cancellationToken).ConfigureAwait(false);
    }

    // PY's `_track_task_lifecycle`. task_started marks a task in flight (only for
    // _deferringTaskTypes: background shells/monitors never reach a terminal status and would
    // withhold stdin forever); task_notification or a task_updated patch with a terminal status
    // clears it. This is a mitigation for #1088, not a complete fix: a task that settles before
    // its own turn's result frame still lets stdin close even though a continuation may follow.
    private void TrackTaskLifecycle(JsonObject message)
    {
        var subtype = JsonHelpers.GetString(message, WireConstants.Keys.Subtype);
        var taskId = JsonHelpers.GetString(message, WireConstants.Keys.TaskId);
        if (string.IsNullOrEmpty(taskId))
        {
            return;
        }

        if (subtype == WireConstants.SystemSubtypes.TaskStarted)
        {
            var taskType = JsonHelpers.GetString(message, WireConstants.Keys.TaskType);
            if (taskType is not null && _deferringTaskTypes.Contains(taskType))
            {
                _inflightTasks.Add(taskId);
            }
        }
        else if (subtype == WireConstants.SystemSubtypes.TaskNotification)
        {
            _inflightTasks.Remove(taskId);
        }
        else if (subtype == WireConstants.SystemSubtypes.TaskUpdated)
        {
            var patch = JsonHelpers.GetObject(message, WireConstants.Keys.Patch);
            var status = patch is not null ? JsonHelpers.GetString(patch, WireConstants.Keys.Status) : null;
            if (status is not null && TaskStatuses.Terminal.Contains(status))
            {
                _inflightTasks.Remove(taskId);
            }
        }
    }

    // PY's `_has_bidirectional_needs`.
    private bool HasBidirectionalNeeds() => _mcpBridges.Count > 0 || _hooks.Count > 0 || _canUseTool is not null;

    // PY's `wait_for_result_and_end_input`.
    public async Task WaitForResultAndEndInputAsync()
    {
        if (HasBidirectionalNeeds())
        {
            SdkLog.WaitingForRunEndingResult(_logger, _mcpBridges.Count, _hooks.Count > 0, _canUseTool is not null);
            await _firstResult.Task.ConfigureAwait(false);
        }

        await _transport.EndInputAsync().ConfigureAwait(false);
    }

    // PY's `stream_input`.
    public async Task StreamInputAsync(IAsyncEnumerable<JsonObject> stream)
    {
        var written = 0;
        try
        {
            await foreach (var message in stream.ConfigureAwait(false))
            {
                if (_closed)
                {
                    break;
                }

                await _transport.WriteAsync(StampUserMessage(message, _verbatimPrompts).ToJsonString() + "\n").ConfigureAwait(false);
                written++;
            }
        }
        catch (Exception e)
        {
            SdkLog.PromptStreamFailedClosingStdin(_logger, e);
        }

        try
        {
            if (written > 0)
            {
                await WaitForResultAndEndInputAsync().ConfigureAwait(false);
            }
            else
            {
                await _transport.EndInputAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            SdkLog.ErrorClosingInputStream(_logger, e);
        }
    }

    // PY's `receive_messages`.
    public async IAsyncEnumerable<JsonObject> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (frame.IsEnd)
            {
                yield break;
            }

            if (frame.Error is not null)
            {
                ExceptionDispatchInfo.Capture(frame.Error).Throw();
            }

            yield return frame.Message!;
        }
    }

    // PY's `close`/`_close_impl`. Idempotent: every step below already tolerates being
    // repeated (a cancelled CancellationTokenSource can be cancelled again, a null _readTask skips
    // the wait, transport.CloseAsync() no-ops once its process is gone), so no extra guard is
    // needed -- matching how PY's own close() gets away with no idempotency flag either.
    public async Task CloseAsync()
    {
        _closed = true;

        if (_mirrorBatcher is not null)
        {
            await _mirrorBatcher.CloseAsync().ConfigureAwait(false);
        }

        // Cancels every child task and in-flight control-request handler: they all observe either
        // this token directly or a linked token source created from it.
        _lifetime.Cancel();

        foreach (var bridge in _mcpBridges.Values)
        {
            await bridge.CloseAsync().ConfigureAwait(false);
        }

        if (_readTask is not null)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _readTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _readTask = null;
        }

        _messages.Writer.TryComplete();
        await _transport.CloseAsync().ConfigureAwait(false);
    }

    // Backstop for CA1001 (owns _lifetime, a CancellationTokenSource): CloseAsync() is the normal,
    // full teardown route, called by every code path that constructs a ControlProtocol; this only
    // releases what CloseAsync() itself never disposes. Cancels first so a read loop iteration
    // still in flight unwinds via its own OperationCanceledException handling instead of a token
    // registration racing disposal (ObjectDisposedException).
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _mirrorBatcher?.Dispose();
    }

    // PY's `stamp_user_message`.
    internal static JsonObject StampUserMessage(JsonObject message, bool verbatimPrompts)
    {
        if (!verbatimPrompts)
        {
            return message;
        }

        var copy = (JsonObject)message.DeepClone();
        copy["client_composed"] = true;
        return copy;
    }

    // PY's `_error_result_text`.
    internal static string ErrorResultText(JsonObject message)
    {
        var errors = ResultException.NormalizeResultErrors(JsonHelpers.GetNode(message, "errors"));
        if (errors.Count > 0)
        {
            return string.Join("; ", errors);
        }

        var result = JsonHelpers.GetString(message, "result");
        if (!string.IsNullOrWhiteSpace(result))
        {
            return result.Trim();
        }

        var subtype = JsonHelpers.GetString(message, "subtype");
        if (!string.IsNullOrEmpty(subtype) && subtype != "success")
        {
            return subtype;
        }

        var status = JsonHelpers.GetInt32(message, "api_error_status");
        return status is not null ? $"API error (HTTP {status})" : "unknown error";
    }
}
