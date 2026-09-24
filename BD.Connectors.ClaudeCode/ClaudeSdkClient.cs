using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Sessions.Internal;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode;

// Literal port of PY's `ClaudeSDKClient`. Bidirectional, stateful, interactive
// conversations: send and receive messages at any time, with interrupt and dynamic control support.
// For one-shot or unidirectional-streaming interactions, use ClaudeAgent.QueryAsync instead.
public sealed class ClaudeSdkClient(ClaudeAgentOptions? options = null, ITransport? transport = null) : IAsyncDisposable
{
    private readonly ITransport? _customTransport = transport;
    private ITransport? _transport;
    private ControlProtocol? _query;
    private bool _verbatimPrompts;
    private SessionResume.MaterializedResume? _materialized;

    public ClaudeAgentOptions Options { get; } = options ?? new ClaudeAgentOptions();

    // PY's `connect`.
    public Task ConnectAsync(string? prompt = null, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(prompt, null, cancellationToken);

    public Task ConnectAsync(IAsyncEnumerable<JsonObject> prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return ConnectCoreAsync(null, prompt, cancellationToken);
    }

    private async Task ConnectCoreAsync(
        string? stringPrompt,
        IAsyncEnumerable<JsonObject>? streamPrompt,
        CancellationToken cancellationToken)
    {
        // Fail fast on invalid session_store option combinations before spawning the subprocess.
        SessionStoreValidation.ValidateSessionStoreOptions(Options);

        try
        {
            await ConnectInnerAsync(stringPrompt, streamPrompt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If connect fails after the subprocess has spawned (e.g. at query.InitializeAsync()),
            // close the subprocess/read task before propagating. DisconnectAsync is None-safe for
            // pre-spawn failures too, so it is reused here just like PY reuses disconnect().
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    // PY's `_connect_inner`.
    private async Task ConnectInnerAsync(
        string? stringPrompt,
        IAsyncEnumerable<JsonObject>? streamPrompt,
        CancellationToken cancellationToken)
    {
        // Validate and configure permission settings (matching the TypeScript SDK logic).
        var options = OptionsConfigurator.ConfigureCanUseTool(Options, Options.Logger);

        _materialized = options.SessionStore is not null
            ? await SessionResume.MaterializeResumeSessionAsync(options, options.Logger, cancellationToken).ConfigureAwait(false)
            : null;
        if (_materialized is not null)
        {
            options = SessionResume.ApplyMaterializedOptions(options, _materialized);
        }

        // Use the provided custom transport or create a subprocess transport.
        _transport = _customTransport ?? new SubprocessCliTransport(options);
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        // Extract SDK MCP servers from options (PY).
        var sdkMcpServers = InternalClient.ExtractSdkMcpServers(options);

        // Calculate the initialize timeout from CLAUDE_CODE_STREAM_CLOSE_TIMEOUT if set
        // (milliseconds, converted to seconds).
        var timeoutMsRaw = Environment.GetEnvironmentVariable(WireConstants.EnvVars.ClaudeCodeStreamCloseTimeout) ?? "60000";
        var timeoutMs = int.Parse(timeoutMsRaw, CultureInfo.InvariantCulture);
        var initializeTimeout = TimeSpan.FromSeconds(Math.Max(timeoutMs / 1000.0, 60.0));

        // Extract exclude_dynamic_sections and snapshot from the system prompt for the initialize
        // request (older CLIs ignore unknown initialize fields).
        bool? excludeDynamicSections = null;
        bool? systemPromptSnapshot = null;
        if (options.SystemPrompt is SystemPromptConfig.Preset preset)
        {
            excludeDynamicSections = preset.ExcludeDynamicSections;
            systemPromptSnapshot = preset.Snapshot;
        }
        else if (options.SystemPrompt is SystemPromptConfig.Custom custom)
        {
            systemPromptSnapshot = custom.Snapshot;
        }

        JsonObject? agentsJson = null;
        if (options.Agents is { Count: > 0 } agents)
        {
            var obj = new JsonObject();
            foreach (var (name, definition) in agents)
            {
                obj[name] = definition.ToJson();
            }

            agentsJson = obj;
        }

        _verbatimPrompts = options.VerbatimPrompts;

        var hooks = options.Hooks is { Count: > 0 } hooksDict ? OptionsConfigurator.HooksToInternal(hooksDict) : null;

        // ClaudeSdkClient always uses streaming mode.
        _query = new ControlProtocol(
            _transport,
            options.CanUseTool,
            hooks,
            sdkMcpServers,
            initializeTimeout,
            agentsJson,
            excludeDynamicSections,
            systemPromptSnapshot,
            options.Skills,
            options.ForwardSubagentText,
            _verbatimPrompts,
            options.Logger);

        if (options.SessionStore is { } sessionStore)
        {
            var query = _query;
            var batcher = SessionResume.BuildMirrorBatcher(
                sessionStore,
                _materialized,
                options.Env,
                (key, error) =>
                {
                    query.ReportMirrorError(key is not null ? SessionKeyJson.ToJson(key) : null, error);
                    return Task.CompletedTask;
                },
                options.SessionStoreFlush,
                options.Logger);
            _query.SetTranscriptMirrorBatcher(batcher);
        }

        // Start reading messages and initialize.
        _query.Start();
        await _query.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // If we have an initial prompt, send it.
        if (stringPrompt is not null)
        {
            var message = new JsonObject
            {
                [WireConstants.Keys.Type] = WireConstants.MessageTypes.User,
                [WireConstants.Keys.Message] = new JsonObject
                {
                    [WireConstants.Keys.Role] = "user",
                    [WireConstants.Keys.Content] = stringPrompt,
                },
                [WireConstants.Keys.ParentToolUseId] = null,
                [WireConstants.Keys.SessionId] = "default",
            };
            await _transport.WriteAsync(
                ControlProtocol.StampUserMessage(message, _verbatimPrompts).ToJsonString() + "\n",
                cancellationToken).ConfigureAwait(false);
        }
        else if (streamPrompt is not null)
        {
            _ = _query.SpawnTask(_ => _query.StreamInputAsync(streamPrompt));
        }
    }

    // PY's `receive_messages`.
    public async IAsyncEnumerable<Message> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = RequireConnected();

        await foreach (var data in query.ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
        {
            var message = MessageParser.Parse(data);
            if (message is not null)
            {
                yield return message;
            }
        }
    }

    // PY's `receive_response`. Stops immediately after yielding a ResultMessage.
    public async IAsyncEnumerable<Message> ReceiveResponseAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
            if (message is ResultMessage)
            {
                yield break;
            }
        }
    }

    // Not part of the PY port. Convenience over ReceiveResponseAsync for callers who want a single
    // object with readonly properties (Text, Result, ToolUses, ...) instead of iterating the
    // message stream themselves.
    public async Task<ClaudeResponse> ReceiveFullResponseAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<Message>();
        await foreach (var message in ReceiveResponseAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(message);
        }

        return new ClaudeResponse(messages);
    }

    // PY's `query`. Sends a new request in streaming mode.
    public async Task QueryAsync(string prompt, string sessionId = "default", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var (query, transport) = RequireConnectedWithTransport();
        _ = query;

        var message = new JsonObject
        {
            [WireConstants.Keys.Type] = WireConstants.MessageTypes.User,
            [WireConstants.Keys.Message] = new JsonObject
            {
                [WireConstants.Keys.Role] = "user",
                [WireConstants.Keys.Content] = prompt,
            },
            [WireConstants.Keys.ParentToolUseId] = null,
            [WireConstants.Keys.SessionId] = sessionId,
        };
        await transport.WriteAsync(
            ControlProtocol.StampUserMessage(message, _verbatimPrompts).ToJsonString() + "\n",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task QueryAsync(IAsyncEnumerable<JsonObject> prompt, string sessionId = "default", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var (query, transport) = RequireConnectedWithTransport();
        _ = query;

        await foreach (var message in prompt.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Ensure session_id is set on each message.
            if (!message.ContainsKey(WireConstants.Keys.SessionId))
            {
                message[WireConstants.Keys.SessionId] = sessionId;
            }

            await transport.WriteAsync(
                ControlProtocol.StampUserMessage(message, _verbatimPrompts).ToJsonString() + "\n",
                cancellationToken).ConfigureAwait(false);
        }
    }

    // Only works in streaming mode (always true for this client).
    public async Task InterruptAsync(CancellationToken cancellationToken = default) =>
        await RequireConnected().InterruptAsync(cancellationToken).ConfigureAwait(false);

    // PY's `set_permission_mode`.
    public async Task SetPermissionModeAsync(PermissionMode mode, CancellationToken cancellationToken = default) =>
        await RequireConnected().SetPermissionModeAsync(mode, cancellationToken).ConfigureAwait(false);

    // PY's `set_model`.
    public async Task SetModelAsync(string? model = null, CancellationToken cancellationToken = default) =>
        await RequireConnected().SetModelAsync(model, cancellationToken).ConfigureAwait(false);

    // PY's `rewind_files`.
    public async Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default) =>
        await RequireConnected().RewindFilesAsync(userMessageId, cancellationToken).ConfigureAwait(false);

    // PY's `reconnect_mcp_server`.
    public async Task ReconnectMcpServerAsync(string serverName, CancellationToken cancellationToken = default) =>
        await RequireConnected().ReconnectMcpServerAsync(serverName, cancellationToken).ConfigureAwait(false);

    // PY's `toggle_mcp_server`.
    public async Task ToggleMcpServerAsync(string serverName, bool enabled, CancellationToken cancellationToken = default) =>
        await RequireConnected().ToggleMcpServerAsync(serverName, enabled, cancellationToken).ConfigureAwait(false);

    // PY's `stop_task`.
    public async Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        await RequireConnected().StopTaskAsync(taskId, cancellationToken).ConfigureAwait(false);

    // PY's `get_mcp_status`.
    public async Task<McpStatusResponse> GetMcpStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await RequireConnected().GetMcpStatusAsync(cancellationToken).ConfigureAwait(false);
        return McpStatusResponse.FromJson(response);
    }

    // PY's `get_context_usage`.
    public async Task<ContextUsageResponse> GetContextUsageAsync(CancellationToken cancellationToken = default)
    {
        var response = await RequireConnected().GetContextUsageAsync(cancellationToken).ConfigureAwait(false);
        return ContextUsageResponse.FromJson(response);
    }

    // PY's `get_server_info`. Unlike the other control methods, this does not raise when not
    // connected -- it simply mirrors PY's `getattr(self._query, "_initialization_result", None)`.
    public JsonObject? GetServerInfo() => _query?.InitializationResult;

    // PY's `disconnect`. Idempotent.
    public async Task DisconnectAsync()
    {
        if (_query is not null)
        {
            await _query.CloseAsync().ConfigureAwait(false);
            _query.Dispose();
            _query = null;
        }

        _transport = null;

        if (_materialized is not null)
        {
            await _materialized.Cleanup().ConfigureAwait(false);
            _materialized = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private ControlProtocol RequireConnected() =>
        _query ?? throw new CliConnectionException("Not connected. Call connect() first.");

    private (ControlProtocol Query, ITransport Transport) RequireConnectedWithTransport()
    {
        if (_query is null || _transport is null)
        {
            throw new CliConnectionException("Not connected. Call connect() first.");
        }

        return (_query, _transport);
    }
}
