using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Mcp;

// The SDK's own small MCP-server dispatcher: unlike PY's build_tool_server, which hands every
// JSON-RPC method to a real `mcp.server.Server` over an in-memory transport, this
// answers initialize/ping/tools/list/tools/call/notifications/cancelled directly -- no MCP library
// dependency, no server session/handshake state machine to replicate.
internal sealed class SdkMcpToolServer : ISdkMcpServer
{
    // Oldest-to-newest MCP protocol versions this dispatcher understands; verify in a contract test
    // which one the CLI actually requests.
    private static readonly string[] _supportedProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];
    private const string LatestProtocolVersion = "2025-11-25";

    // PY's SHUTDOWN_GRACE_SECONDS.
    private static readonly TimeSpan _shutdownGrace = TimeSpan.FromSeconds(5);

    private readonly string _version;
    private readonly IReadOnlyList<SdkMcpTool> _tools;
    private readonly Dictionary<string, SdkMcpTool> _toolsByName;
    private readonly ILogger _logger;

    // Keyed by the JSON-RPC request id's canonical JSON text (ids may be strings or numbers) so a
    // notifications/cancelled message naming that id can cancel the matching in-flight tools/call.
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, TaskCompletionSource Done)> _activeCalls = new(StringComparer.Ordinal);

    public SdkMcpToolServer(string name, string version, IReadOnlyList<SdkMcpTool> tools, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(tools);

        Name = name;
        _version = version;
        _tools = tools;
        _toolsByName = tools.ToDictionary(tool => tool.Name, tool => tool, StringComparer.Ordinal);
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name { get; }

    public async Task<JsonObject?> HandleAsync(JsonObject jsonRpcMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jsonRpcMessage);

        var method = JsonHelpers.GetString(jsonRpcMessage, "method");
        var hasId = jsonRpcMessage.ContainsKey("id");
        var id = JsonHelpers.GetNode(jsonRpcMessage, "id");

        switch (method)
        {
            case "initialize":
                return BuildResponse(id, BuildInitializeResult(jsonRpcMessage));

            case "ping":
                return BuildResponse(id, []);

            case "tools/list":
                return BuildResponse(id, BuildToolsListResult());

            case "tools/call":
                return await HandleToolCallAsync(id, jsonRpcMessage, cancellationToken).ConfigureAwait(false);

            case "notifications/cancelled":
                HandleCancelled(jsonRpcMessage);
                return null;

            default:
                // Any other notification (including "notifications/initialized") gets no reply;
                // any other request is a method this dispatcher does not implement.
                return hasId ? BuildErrorResponse(id, -32601, "Method not found") : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var pending = _activeCalls.Values.ToArray();
        foreach (var (cts, _) in pending)
        {
            TryCancel(cts);
        }

        if (pending.Length == 0)
        {
            return;
        }

        var allDone = Task.WhenAll(pending.Select(call => call.Done.Task));
        await Task.WhenAny(allDone, Task.Delay(_shutdownGrace)).ConfigureAwait(false);
    }

    private JsonObject BuildInitializeResult(JsonObject message)
    {
        var requestedVersion = JsonHelpers.GetObject(message, "params") is { } parameters
            ? JsonHelpers.GetString(parameters, "protocolVersion")
            : null;
        var protocolVersion = requestedVersion is not null && _supportedProtocolVersions.Contains(requestedVersion, StringComparer.Ordinal)
            ? requestedVersion
            : LatestProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = _version },
        };
    }

    private JsonObject BuildToolsListResult()
    {
        var entries = new List<JsonNode?>();
        foreach (var tool in _tools)
        {
            var entry = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            };

            if (BuildAnnotations(tool.Annotations) is { } annotationsJson)
            {
                entry["annotations"] = annotationsJson;
            }

            if (BuildMeta(tool.Annotations) is { } metaJson)
            {
                entry["_meta"] = metaJson;
            }

            entries.Add(entry);
        }

        return new JsonObject { ["tools"] = new JsonArray([.. entries]) };
    }

    private async Task<JsonObject> HandleToolCallAsync(JsonNode? id, JsonObject message, CancellationToken cancellationToken)
    {
        var idKey = IdKey(id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeCalls[idKey] = (cts, done);

        try
        {
            var parameters = JsonHelpers.GetObject(message, "params") ?? [];
            var toolName = JsonHelpers.GetString(parameters, "name") ?? string.Empty;
            var arguments = JsonHelpers.GetObject(parameters, "arguments") ?? [];

            if (!_toolsByName.TryGetValue(toolName, out var tool))
            {
                return BuildResponse(id, ToolErrorResult($"Tool '{toolName}' not found"));
            }

            var validationError = JsonSchemaValidator.Validate(arguments, tool.InputSchema);
            if (validationError is not null)
            {
                return BuildResponse(id, ToolErrorResult($"Input validation error: {validationError}"));
            }

            try
            {
                var result = await tool.Handler(arguments, cts.Token).ConfigureAwait(false);
                return BuildResponse(id, BuildToolCallResult(result));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return BuildErrorResponse(id, -32800, "Request cancelled");
            }
            catch (Exception e)
            {
                return BuildResponse(id, ToolErrorResult(e.Message));
            }
        }
        finally
        {
            _activeCalls.TryRemove(idKey, out _);
            done.TrySetResult();
            cts.Dispose();
        }
    }

    private void HandleCancelled(JsonObject message)
    {
        var requestId = JsonHelpers.GetObject(message, "params") is { } parameters
            ? JsonHelpers.GetNode(parameters, "requestId")
            : null;
        if (requestId is null)
        {
            return;
        }

        if (_activeCalls.TryGetValue(IdKey(requestId), out var call))
        {
            TryCancel(call.Cts);
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call finished and disposed its token source between the snapshot and this call.
        }
    }

    private JsonArray ConvertToolContent(IReadOnlyList<McpContent> items)
    {
        var content = new List<JsonNode?>();
        foreach (var item in items)
        {
            switch (item)
            {
                case McpTextContent text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;

                case McpImageContent image:
                    content.Add(new JsonObject { ["type"] = "image", ["data"] = image.Data, ["mimeType"] = image.MimeType });
                    break;

                case McpResourceLinkContent link:
                    var parts = new[] { link.Name, link.Uri, link.Description }.Where(part => !string.IsNullOrEmpty(part));
                    var joined = string.Join("\n", parts);
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = joined.Length > 0 ? joined : "Resource link" });
                    break;

                case McpEmbeddedResourceContent { Text: { } text }:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    break;

                case McpEmbeddedResourceContent:
                    SdkLog.BinaryEmbeddedResourceSkipped(_logger);
                    break;
            }
        }

        return new JsonArray([.. content]);
    }

    private static JsonObject? BuildAnnotations(ToolAnnotations? annotations)
    {
        if (annotations is null)
        {
            return null;
        }

        var json = new JsonObject();
        if (annotations.Title is { } title)
        {
            json["title"] = title;
        }

        if (annotations.ReadOnlyHint is { } readOnlyHint)
        {
            json["readOnlyHint"] = readOnlyHint;
        }

        if (annotations.DestructiveHint is { } destructiveHint)
        {
            json["destructiveHint"] = destructiveHint;
        }

        if (annotations.IdempotentHint is { } idempotentHint)
        {
            json["idempotentHint"] = idempotentHint;
        }

        if (annotations.OpenWorldHint is { } openWorldHint)
        {
            json["openWorldHint"] = openWorldHint;
        }

        return json.Count > 0 ? json : null;
    }

    // MaxResultSizeChars is not an MCP hint but a Claude Code one, carried in _meta rather than
    // annotations (matches PY).
    private static JsonObject? BuildMeta(ToolAnnotations? annotations) =>
        annotations?.MaxResultSizeChars is { } maxSize
            ? new JsonObject { ["anthropic/maxResultSizeChars"] = maxSize }
            : null;

    private static JsonObject ToolErrorResult(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    private JsonObject BuildToolCallResult(McpToolResult result) => new()
    {
        ["content"] = ConvertToolContent(result.Content),
        ["isError"] = result.IsError,
    };

    private static JsonObject BuildResponse(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject BuildErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static string IdKey(JsonNode? id) => id?.ToJsonString() ?? "null";
}
