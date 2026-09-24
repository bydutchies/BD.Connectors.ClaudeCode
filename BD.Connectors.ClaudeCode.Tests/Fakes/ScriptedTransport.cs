using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Tests.Fakes;

// ITransport test fake for ControlProtocol unit tests (F3.1). Records every WriteAsync payload
// (parsed) and whether EndInputAsync/CloseAsync were called; stdout is fed on demand via Emit,
// with Complete ending the read side (optionally with a read error).
internal sealed class ScriptedTransport : ITransport
{
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    private readonly Lock _gate = new();
    private readonly List<JsonObject> _writes = [];
    private readonly List<(Func<JsonObject, bool> Predicate, TaskCompletionSource<JsonObject> Tcs)> _waiters = [];
    private readonly TaskCompletionSource _endInputTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _readError;

    public bool AutoRespondInitialize { get; init; }

    public bool IsReady { get; private set; } = true;

    public bool EndInputCalled { get; private set; }

    public bool CloseCalled { get; private set; }

    // Lets a test await "EndInputAsync was called" instead of polling EndInputCalled.
    public Task EndInputSignal => _endInputTcs.Task;

    public IReadOnlyList<JsonObject> Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsReady = true;
        return Task.CompletedTask;
    }

    public Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        var obj = (JsonObject)JsonNode.Parse(data)!;
        List<TaskCompletionSource<JsonObject>> matched = [];
        lock (_gate)
        {
            _writes.Add(obj);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Predicate(obj))
                {
                    matched.Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var tcs in matched)
        {
            tcs.TrySetResult(obj);
        }

        if (AutoRespondInitialize
            && obj["type"]?.GetValue<string>() == "control_request"
            && obj["request"] is JsonObject request
            && request["subtype"]?.GetValue<string>() == "initialize")
        {
            var requestId = obj["request_id"]!.GetValue<string>();
            Emit(new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = requestId,
                    ["response"] = new JsonObject { ["commands"] = new JsonArray() },
                },
            });
        }

        return Task.CompletedTask;
    }

    public void Emit(JsonObject message) => _incoming.Writer.TryWrite(message);

    public void Complete(Exception? error = null)
    {
        _readError = error;
        _incoming.Writer.TryComplete();
    }

    public async IAsyncEnumerable<JsonObject> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }

        if (_readError is not null)
        {
            throw _readError;
        }
    }

    public Task CloseAsync()
    {
        CloseCalled = true;
        IsReady = false;
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        EndInputCalled = true;
        _endInputTcs.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task<JsonObject> WaitForWriteAsync(Func<JsonObject, bool> predicate, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var existing = _writes.FirstOrDefault(predicate);
            if (existing is not null)
            {
                return existing;
            }

            _waiters.Add((predicate, tcs));
        }

        using var cts = new CancellationTokenSource(timeout);
        await using var registration = cts.Token.Register(() => tcs.TrySetException(new TimeoutException("Timed out waiting for a matching write.")));
        return await tcs.Task.ConfigureAwait(false);
    }
}
