using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Mcp;

// Full implementation (dispatcher, bridge) lands in F5; only the shape is declared here
// because Options/McpServerConfig.cs (F1.8) needs it for McpServerConfig.Sdk.
public interface ISdkMcpServer
{
    string Name { get; }

    // null return means "no response" (a notification was handled).
    Task<JsonObject?> HandleAsync(JsonObject jsonRpcMessage, CancellationToken cancellationToken);

    ValueTask DisposeAsync();
}
