using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Sessions;

// In-memory ISessionStore implementation for testing and development. Stores entries in a dictionary
// keyed by a composite "project_key/session_id" string (with an optional "/subpath" suffix). Not
// suitable for production -- data is lost when the process exits. Ported from PY's
// `InMemorySessionStore`.
public sealed class InMemorySessionStore : ISessionStore, ISessionStoreListing, ISessionStoreSummaries, ISessionStoreDeletion, ISessionStoreSubkeys
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<JsonObject>> _store = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _mtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ProjectKey, string SessionId), SessionSummaryEntry> _summaries = [];
    private long _lastMtime;

    private static string KeyToString(SessionKey key) =>
        string.IsNullOrEmpty(key.Subpath) ? $"{key.ProjectKey}/{key.SessionId}" : $"{key.ProjectKey}/{key.SessionId}/{key.Subpath}";

    // Storage write time for this adapter, in Unix epoch ms. Guaranteed strictly monotonically
    // increasing across calls within the process so back-to-back appends always produce distinct
    // mtimes (matches PY).
    private long NextMtime()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs <= _lastMtime)
        {
            nowMs = _lastMtime + 1;
        }

        _lastMtime = nowMs;
        return nowMs;
    }

    public Task AppendAsync(SessionKey key, IReadOnlyList<JsonObject> entries, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var k = KeyToString(key);
            if (!_store.TryGetValue(k, out var list))
            {
                list = [];
                _store[k] = list;
            }

            list.AddRange(entries);
            var nowMs = NextMtime();

            // Maintain the per-session summary sidecar incrementally so ListSessionSummariesAsync
            // never re-reads. Subagent subpaths don't contribute to the main session's summary.
            if (string.IsNullOrEmpty(key.Subpath))
            {
                var sk = (key.ProjectKey, key.SessionId);
                _summaries.TryGetValue(sk, out var prev);
                var folded = SessionSummary.Fold(prev, key, entries);
                _summaries[sk] = folded with { Mtime = nowMs };
            }

            _mtimes[k] = nowMs;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JsonObject>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<JsonObject>? result = _store.TryGetValue(KeyToString(key), out var entries) ? entries.ToList() : null;
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<SessionStoreListEntry>> ListSessionsAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var prefix = projectKey + "/";
            var results = new List<SessionStoreListEntry>();
            foreach (var k in _store.Keys)
            {
                if (!k.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = k[prefix.Length..];

                // Only include main transcripts (no subpath, so no second '/').
                if (!rest.Contains('/'))
                {
                    results.Add(new SessionStoreListEntry(rest, _mtimes.GetValueOrDefault(k, 0)));
                }
            }

            return Task.FromResult<IReadOnlyList<SessionStoreListEntry>>(results);
        }
    }

    public Task<IReadOnlyList<SessionSummaryEntry>> ListSessionSummariesAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<SessionSummaryEntry> results = [.. _summaries.Where(kv => kv.Key.ProjectKey == projectKey).Select(kv => kv.Value)];
            return Task.FromResult(results);
        }
    }

    public Task DeleteAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var k = KeyToString(key);
            _store.Remove(k);
            _mtimes.Remove(k);

            // Deleting the main transcript cascades to its subkeys (subagent transcripts, metadata)
            // so they aren't orphaned. A targeted delete with an explicit subpath removes only that
            // one entry.
            if (string.IsNullOrEmpty(key.Subpath))
            {
                _summaries.Remove((key.ProjectKey, key.SessionId));
                var prefix = $"{key.ProjectKey}/{key.SessionId}/";
                foreach (var storeKey in _store.Keys.Where(sk => sk.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                {
                    _store.Remove(storeKey);
                    _mtimes.Remove(storeKey);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListSubkeysAsync(SessionListSubkeysKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var prefix = $"{key.ProjectKey}/{key.SessionId}/";
            IReadOnlyList<string> results = [.. _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..])];
            return Task.FromResult(results);
        }
    }

    // ------------------------------------------------------------------
    // Test helpers (matches PY)
    // ------------------------------------------------------------------

    public IReadOnlyList<JsonObject> GetEntries(SessionKey key)
    {
        lock (_gate)
        {
            return _store.TryGetValue(KeyToString(key), out var list) ? list.ToList() : [];
        }
    }

    public int Size
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var k in _store.Keys)
                {
                    var firstSlash = k.IndexOf('/');
                    if (firstSlash != -1 && !k[(firstSlash + 1)..].Contains('/'))
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _store.Clear();
            _mtimes.Clear();
            _summaries.Clear();
            _lastMtime = 0;
        }
    }
}
