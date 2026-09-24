using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Logging;
using BD.Connectors.ClaudeCode.Sessions;
using BD.Connectors.ClaudeCode.Sessions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Internal;

// Batching layer between transcript_mirror stdout frames and an ISessionStore. Ported from PY. The
// CLI subprocess emits
// {"type":"transcript_mirror","filePath":...,"entries":[...]} frames interleaved with normal SDK
// messages; ControlProtocol's read loop peels these off and hands them to Enqueue, which accumulates
// them and flushes to ISessionStore.AppendAsync either when a `result` message arrives (explicit
// FlushAsync) or when the pending buffer exceeds size thresholds (eager background flush). This keeps
// adapter latency off the hot path during model streaming.
internal sealed class TranscriptMirrorBatcher : IDisposable
{
    // Eager-flush thresholds (matches PY's own defaults).
    internal const int DefaultMaxPendingEntries = 500;
    internal const int DefaultMaxPendingBytes = 1 << 20; // 1 MiB
    private static readonly TimeSpan _defaultSendTimeout = TimeSpan.FromSeconds(60);

    // Bounded retry for transient adapter failures.
    private const int MirrorAppendMaxAttempts = 3;
    private static readonly TimeSpan[] _mirrorAppendBackoff = [TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.8)];

    private readonly ISessionStore _store;
    private readonly string _projectsDir;
    private readonly Func<SessionKey?, string, Task> _onError;
    private readonly TimeSpan _sendTimeout;
    private readonly int _maxPendingEntries;
    private readonly int _maxPendingBytes;
    private readonly ILogger _logger;

    private readonly object _pendingGate = new();
    private List<MirrorEntry> _pending = [];
    private int _pendingEntries;
    private int _pendingBytes;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Set by Enqueue's eager background flush; not awaited by this class itself (PY: fire-and-forget
    // too), kept only so a caller (e.g. tests) can observe or await the most recent eager flush.
    internal Task? LastEagerFlushTask { get; private set; }

    private readonly record struct MirrorEntry(string FilePath, List<JsonObject> Entries, int Bytes);

    public TranscriptMirrorBatcher(
        ISessionStore store,
        string projectsDir,
        Func<SessionKey?, string, Task> onError,
        TimeSpan? sendTimeout = null,
        int maxPendingEntries = DefaultMaxPendingEntries,
        int maxPendingBytes = DefaultMaxPendingBytes,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projectsDir);
        ArgumentNullException.ThrowIfNull(onError);

        _store = store;
        _projectsDir = projectsDir;
        _onError = onError;
        _sendTimeout = sendTimeout ?? _defaultSendTimeout;
        _maxPendingEntries = maxPendingEntries;
        _maxPendingBytes = maxPendingBytes;
        _logger = logger ?? NullLogger.Instance;
    }

    // Backstop for CA1001 (owns _lock, a SemaphoreSlim). CloseAsync() is the normal teardown route
    // (called by ControlProtocol.CloseAsync()); the owning ControlProtocol also disposes this from its
    // own Dispose() once closed.
    public void Dispose() => _lock.Dispose();

    // Buffer a frame; schedule an eager flush if thresholds are exceeded (PY's `enqueue`).
    public void Enqueue(string filePath, JsonArray entries)
    {
        var objects = new List<JsonObject>(entries.Count);
        foreach (var node in entries)
        {
            if (node is JsonObject obj)
            {
                objects.Add(obj);
            }
        }

        // Approximate wire size -- one stringify per frame (not per entry) keeps this cheap relative
        // to the parse the transport already did. UTF-16 char count, not UTF-8 byte count; a threshold
        // heuristic only, not a wire-format concern.
        var size = entries.ToJsonString().Length;

        bool exceeded;
        lock (_pendingGate)
        {
            _pending.Add(new MirrorEntry(filePath, objects, size));
            _pendingEntries += objects.Count;
            _pendingBytes += size;
            exceeded = _pendingEntries > _maxPendingEntries || _pendingBytes > _maxPendingBytes;
        }

        if (exceeded)
        {
            LastEagerFlushTask = Task.Run(DrainAsync);
        }
    }

    // Flush all pending entries, serialized after any in-flight eager flush.
    public Task FlushAsync() => DrainAsync();

    // Final flush before teardown. Never raises.
    public async Task CloseAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SdkLog.MirrorCloseFlushFailed(_logger, e);
        }
    }

    // Detach the pending buffer, await any prior flush, then send. Never raises -- adapter and
    // on_error callback errors are caught and logged (PY's `_drain`).
    private async Task DrainAsync()
    {
        List<MirrorEntry> items;
        lock (_pendingGate)
        {
            items = _pending;
            _pending = [];
            _pendingEntries = 0;
            _pendingBytes = 0;
        }

        var errors = new List<(SessionKey Key, string Message)>();

        // Detaching happens before acquiring the lock so Enqueue can keep accumulating into a fresh
        // buffer while a prior flush is in flight. The lock is still acquired (and awaited) even for
        // an empty batch so FlushAsync/CloseAsync always observe completion of a prior in-flight
        // eager flush before returning.
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (items.Count == 0)
            {
                return;
            }

            try
            {
                await DoFlushAsync(items, errors).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // DoFlushAsync already wraps store.AppendAsync; this guards any remaining unguarded
                // path so the "never raises" contract holds against future regressions.
                SdkLog.MirrorDrainRaised(_logger, e);
                return;
            }
        }
        finally
        {
            _lock.Release();
        }

        // Report errors after releasing the lock so a slow on_error callback cannot block subsequent
        // drains (which only need the lock for append-ordering).
        foreach (var (key, message) in errors)
        {
            try
            {
                await _onError(key, message).ConfigureAwait(false);
            }
            catch (Exception cbErr)
            {
                SdkLog.MirrorOnErrorCallbackFailed(_logger, cbErr);
            }
        }
    }

    private async Task DoFlushAsync(List<MirrorEntry> items, List<(SessionKey Key, string Message)> errors)
    {
        // Coalesce by file_path so each unique file gets one append per flush instead of one per
        // enqueued frame. Preserves first-seen file order; entries within a path keep enqueue order.
        var order = new List<string>();
        var byPath = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!byPath.TryGetValue(item.FilePath, out var bucket))
            {
                bucket = [];
                byPath[item.FilePath] = bucket;
                order.Add(item.FilePath);
            }

            bucket.AddRange(item.Entries);
        }

        foreach (var filePath in order)
        {
            var entries = byPath[filePath];
            if (entries.Count == 0)
            {
                // Avoid creating phantom keys in adapters that touch storage on append([]) -- nothing
                // to write.
                continue;
            }

            var key = SessionStoreFileKeys.FilePathToSessionKey(filePath, _projectsDir);
            if (key is null)
            {
                SdkLog.MirrorAppendFrameNotUnderProjectsDir(_logger, filePath, _projectsDir);
                continue;
            }

            Exception? lastErr = null;
            var succeeded = false;
            for (var attempt = 0; attempt < MirrorAppendMaxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(_mirrorAppendBackoff[attempt - 1]).ConfigureAwait(false);
                }

                try
                {
                    // WaitAsync (not a CancellationToken passed to AppendAsync) matches PY's
                    // anyio.fail_after: cancellation is best-effort for adapters wrapping
                    // non-cancellable I/O, so the in-flight call may still land after this returns.
                    await _store.AppendAsync(key, entries, CancellationToken.None).WaitAsync(_sendTimeout).ConfigureAwait(false);
                    succeeded = true;
                    break;
                }
                catch (TimeoutException e)
                {
                    // Don't retry on timeout: a retry would launch a concurrent duplicate. Also keeps
                    // worst-case lock hold at ~send_timeout rather than ~3x send_timeout + backoff.
                    lastErr = e;
                    SdkLog.MirrorAppendTimedOut(_logger, _sendTimeout.TotalSeconds, filePath);
                    break;
                }
                catch (Exception e)
                {
                    // Adapter is user code.
                    lastErr = e;
                    SdkLog.MirrorAppendAttemptFailed(_logger, attempt + 1, MirrorAppendMaxAttempts, filePath, e);
                }
            }

            if (!succeeded)
            {
                SdkLog.MirrorAppendFlushFailed(_logger, filePath, lastErr!);
                errors.Add((key, lastErr!.Message));
            }
        }
    }
}
