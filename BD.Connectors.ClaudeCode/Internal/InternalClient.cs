using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Sessions.Internal;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Internal;

// Literal port of PY's `InternalClient`.
//
// PY splits `process_query` (owns resume materialization cleanup) from `_process_query_inner`
// (does the actual work) so the subprocess always closes before the temp CLAUDE_CONFIG_DIR it
// reads/writes is removed. `ProcessQueryCoreAsync` is `process_query`; `ProcessQueryInnerAsync` is
// `_process_query_inner`.
internal static class InternalClient
{
    public static IAsyncEnumerable<Message> ProcessQueryAsync(
        string prompt,
        ClaudeAgentOptions options,
        ITransport? transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return ProcessQueryCoreAsync(prompt, null, options, transport, cancellationToken);
    }

    public static IAsyncEnumerable<Message> ProcessQueryAsync(
        IAsyncEnumerable<JsonObject> prompt,
        ClaudeAgentOptions options,
        ITransport? transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return ProcessQueryCoreAsync(null, prompt, options, transport, cancellationToken);
    }

    private static async IAsyncEnumerable<Message> ProcessQueryCoreAsync(
        string? stringPrompt,
        IAsyncEnumerable<JsonObject>? streamPrompt,
        ClaudeAgentOptions options,
        ITransport? transport,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Fail fast on invalid session_store option combinations before spawning the subprocess.
        SessionStoreValidation.ValidateSessionStoreOptions(options);

        var configuredOptions = OptionsConfigurator.ConfigureCanUseTool(options, options.Logger);

        var materialized = configuredOptions.SessionStore is not null
            ? await SessionResume.MaterializeResumeSessionAsync(configuredOptions, configuredOptions.Logger, cancellationToken).ConfigureAwait(false)
            : null;

        var effectiveOptions = materialized is not null
            ? SessionResume.ApplyMaterializedOptions(configuredOptions, materialized)
            : configuredOptions;

        try
        {
            await foreach (var message in ProcessQueryInnerAsync(stringPrompt, streamPrompt, effectiveOptions, materialized, transport, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            if (materialized is not null)
            {
                await materialized.Cleanup().ConfigureAwait(false);
            }
        }
    }

    private static async IAsyncEnumerable<Message> ProcessQueryInnerAsync(
        string? stringPrompt,
        IAsyncEnumerable<JsonObject>? streamPrompt,
        ClaudeAgentOptions configuredOptions,
        SessionResume.MaterializedResume? materialized,
        ITransport? transport,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chosenTransport = transport ?? new SubprocessCliTransport(configuredOptions);
        await chosenTransport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        // Extract SDK MCP servers from options (PY).
        var sdkMcpServers = ExtractSdkMcpServers(configuredOptions);

        // Extract exclude_dynamic_sections and snapshot from the system prompt for the initialize
        // request (older CLIs ignore unknown initialize fields).
        bool? excludeDynamicSections = null;
        bool? systemPromptSnapshot = null;
        if (configuredOptions.SystemPrompt is SystemPromptConfig.Preset preset)
        {
            excludeDynamicSections = preset.ExcludeDynamicSections;
            systemPromptSnapshot = preset.Snapshot;
        }
        else if (configuredOptions.SystemPrompt is SystemPromptConfig.Custom custom)
        {
            systemPromptSnapshot = custom.Snapshot;
        }

        JsonObject? agentsJson = null;
        if (configuredOptions.Agents is { Count: > 0 } agents)
        {
            var obj = new JsonObject();
            foreach (var (name, definition) in agents)
            {
                obj[name] = definition.ToJson();
            }

            agentsJson = obj;
        }

        // Match ClaudeSdkClient.ConnectAsync (F4) -- without this, QueryAsync ignores the env var.
        var timeoutMsRaw = Environment.GetEnvironmentVariable(WireConstants.EnvVars.ClaudeCodeStreamCloseTimeout) ?? "60000";
        var timeoutMs = int.Parse(timeoutMsRaw, CultureInfo.InvariantCulture);
        var initializeTimeout = TimeSpan.FromSeconds(Math.Max(timeoutMs / 1000.0, 60.0));

        var hooks = configuredOptions.Hooks is { Count: > 0 } hooksDict ? OptionsConfigurator.HooksToInternal(hooksDict) : null;

        // Always use streaming mode internally (matching the TypeScript SDK): this ensures agents
        // are always sent via the initialize request.
        var protocol = new ControlProtocol(
            chosenTransport,
            configuredOptions.CanUseTool,
            hooks,
            sdkMcpServers,
            initializeTimeout,
            agentsJson,
            excludeDynamicSections,
            systemPromptSnapshot,
            configuredOptions.Skills,
            configuredOptions.ForwardSubagentText,
            configuredOptions.VerbatimPrompts,
            configuredOptions.Logger);

        if (configuredOptions.SessionStore is { } sessionStore)
        {
            var batcher = SessionResume.BuildMirrorBatcher(
                sessionStore,
                materialized,
                configuredOptions.Env,
                (key, error) =>
                {
                    protocol.ReportMirrorError(key is not null ? SessionKeyJson.ToJson(key) : null, error);
                    return Task.CompletedTask;
                },
                configuredOptions.SessionStoreFlush,
                configuredOptions.Logger);
            protocol.SetTranscriptMirrorBatcher(batcher);
        }

        try
        {
            protocol.Start();

            // Always initialize to send agents via stdin (matching the TypeScript SDK).
            await protocol.InitializeAsync(cancellationToken).ConfigureAwait(false);

            if (stringPrompt is not null)
            {
                // For string prompts, write the user message to stdin after initialize (matching
                // the TypeScript SDK).
                var userMessage = new JsonObject
                {
                    [WireConstants.Keys.Type] = WireConstants.MessageTypes.User,
                    [WireConstants.Keys.SessionId] = string.Empty,
                    [WireConstants.Keys.Message] = new JsonObject
                    {
                        [WireConstants.Keys.Role] = "user",
                        [WireConstants.Keys.Content] = stringPrompt,
                    },
                    [WireConstants.Keys.ParentToolUseId] = null,
                };

                await chosenTransport.WriteAsync(
                    ControlProtocol.StampUserMessage(userMessage, configuredOptions.VerbatimPrompts).ToJsonString() + "\n",
                    cancellationToken).ConfigureAwait(false);

                _ = protocol.SpawnTask(_ => protocol.WaitForResultAndEndInputAsync());
            }
            else if (streamPrompt is not null)
            {
                _ = protocol.SpawnTask(_ => protocol.StreamInputAsync(streamPrompt));
            }

            await foreach (var data in protocol.ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = MessageParser.Parse(data);
                if (message is not null)
                {
                    yield return message;
                }
            }
        }
        finally
        {
            await protocol.CloseAsync().ConfigureAwait(false);
            protocol.Dispose();
        }
    }

    // Mirrors PY's inline MCP-server extraction logic.
    internal static IReadOnlyDictionary<string, ISdkMcpServer> ExtractSdkMcpServers(ClaudeAgentOptions options)
    {
        Dictionary<string, ISdkMcpServer>? servers = null;
        foreach (var (name, config) in options.McpServers)
        {
            if (config is McpSdkServerConfig sdkConfig)
            {
                servers ??= [];
                servers[name] = sdkConfig.Instance;
            }
        }

        return servers ?? new Dictionary<string, ISdkMcpServer>();
    }
}
