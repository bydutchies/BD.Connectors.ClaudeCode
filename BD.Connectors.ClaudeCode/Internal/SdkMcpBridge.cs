using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Logging;
using BD.Connectors.ClaudeCode.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Internal;

// Thin wrapper turning one mcp_message control_request into an ISdkMcpServer.HandleAsync call and
// mapping any handler exception to a JSON-RPC error naming the failed message's own id -- see PY's
// SdkMcpBridge class. Unlike PY, this never runs its own protocol session
// (initialize/tools handshake, in-memory transport, request tracking): SdkMcpToolServer is itself a
// full ISdkMcpServer that answers every JSON-RPC method directly, and owns its own in-flight-call
// cancellation/shutdown bookkeeping, so PY's `_Session` machinery has nothing left to do here. There
// is accordingly no external CancellationToken to thread through HandleAsync either: the two
// cancellation paths (notifications/cancelled, and DisposeAsync's own grace period) are both handled
// entirely inside the dispatcher.
internal sealed class SdkMcpBridge
{
    private const int JsonRpcInternalErrorCode = -32603;

    private readonly string _name;
    private readonly ISdkMcpServer _server;
    private readonly ILogger _logger;

    public SdkMcpBridge(string name, ISdkMcpServer server, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(server);

        _name = name;
        _server = server;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<JsonObject?> HandleAsync(JsonObject message)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            return await _server.HandleAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SdkLog.McpServerFailed(_logger, _name, e);
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonHelpers.GetNode(message, "id")?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = JsonRpcInternalErrorCode,
                    ["message"] = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message,
                },
            };
        }
    }

    public async Task CloseAsync() => await _server.DisposeAsync().ConfigureAwait(false);
}
