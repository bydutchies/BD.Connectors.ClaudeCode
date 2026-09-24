using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Transport;

// WARNING: exposed for custom transport implementations (e.g. remote Claude Code connections).
// May change or be removed in any future release; custom implementations must track interface changes.
// Low-level raw I/O with the Claude process or service. ControlProtocol (F3) builds the control
// protocol and message routing on top of this.
public interface ITransport
{
    Task ConnectAsync(CancellationToken cancellationToken = default);

    // data = JSON + "\n"
    Task WriteAsync(string data, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JsonObject> ReadMessagesAsync(CancellationToken cancellationToken = default);

    // Contract: called from a context where the caller does not depend on external cancellation to
    // make progress. Implementations must bound their own awaits (own timeouts) rather than relying
    // on the caller to cancel them, and must run cleanup to completion even if the caller's own
    // cancellation token is already signalled by the time this is called.
    Task CloseAsync();

    bool IsReady { get; }

    // End the input stream (close stdin for process transports).
    Task EndInputAsync(CancellationToken cancellationToken = default);
}
