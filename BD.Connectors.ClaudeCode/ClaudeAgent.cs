using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode;

// Literal port of PY's module-level `query()` function. One-shot or unidirectional-streaming
// interactions with Claude Code: all input is sent upfront (or, for a stream prompt, produced
// independently of the responses) and there is no conversation state or interrupt capability. For
// interactive, stateful conversations with follow-ups and interrupts, use ClaudeSdkClient instead.
public static class ClaudeAgent
{
    public static IAsyncEnumerable<Message> QueryAsync(
        string prompt,
        ClaudeAgentOptions? options = null,
        ITransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return InternalClient.ProcessQueryAsync(prompt, options ?? new ClaudeAgentOptions(), transport, cancellationToken);
    }

    public static IAsyncEnumerable<Message> QueryAsync(
        IAsyncEnumerable<JsonObject> prompt,
        ClaudeAgentOptions? options = null,
        ITransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return InternalClient.ProcessQueryAsync(prompt, options ?? new ClaudeAgentOptions(), transport, cancellationToken);
    }
}
