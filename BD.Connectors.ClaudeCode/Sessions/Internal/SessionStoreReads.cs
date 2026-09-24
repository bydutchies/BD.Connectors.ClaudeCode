using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// ListSessionsFromStoreAsync / GetSessionInfoFromStoreAsync / GetSessionMessagesFromStoreAsync /
// ListSubagentsFromStoreAsync / GetSubagentMessagesFromStoreAsync -- async, store-backed
// counterparts to the local (disk) reads in SessionsCore/SubagentSessions. Ported from PY.
internal static class SessionStoreReads
{
    // Bounds concurrent SessionStore.load() calls during a list_sessions_from_store slow-path/gap-
    // fill pass so large listings don't exhaust adapter connection pools or hit backend rate limits.
    private const int StoreListLoadConcurrency = 16;

    public static async Task<IReadOnlyList<SdkSessionInfo>> ListSessionsFromStoreAsync(
        ISessionStore store, string? directory, int? limit, int offset, ILogger? logger, CancellationToken cancellationToken)
    {
        var projectPath = SessionPaths.CanonicalizePath(directory ?? ".");
        var projectKey = SessionPaths.SanitizePath(projectPath);
        var hasListSessions = store is ISessionStoreListing;

        // Fast path: if the store maintains incremental summaries, fetch them in one call instead of
        // N per-session load()s.
        if (store is ISessionStoreSummaries summaryStore)
        {
            var summaries = await summaryStore.ListSessionSummariesAsync(projectKey, cancellationToken).ConfigureAwait(false);
            return await ListFromSummariesAsync(store, hasListSessions, summaries, projectKey, projectPath, directory, limit, offset, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!hasListSessions)
        {
            throw new ArgumentException(
                "session_store implements neither list_session_summaries() nor list_sessions() -- cannot list sessions. Provide a store with at least one of those methods.");
        }

        // Copy -- store.list_sessions() may return a reference to internal state.
        var listing = (await ((ISessionStoreListing)store).ListSessionsAsync(projectKey, cancellationToken).ConfigureAwait(false)).ToList();

        // Derive a real summary per session by loading its entries and reusing the filesystem path's
        // lite-parse. Filtering (sidechain/empty drop) happens before pagination so limit/offset index
        // the same filtered set as the disk path.
        var results = await DeriveInfosViaLoadAsync(store, listing, directory, projectPath, cancellationToken).ConfigureAwait(false);
        return ApplySortLimitOffset(results, limit, offset);
    }

    public static async Task<SdkSessionInfo?> GetSessionInfoFromStoreAsync(
        ISessionStore store, string sessionId, string? directory, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return null;
        }

        var jsonl = await LoadStoreEntriesAsJsonlAsync(store, sessionId, directory, cancellationToken).ConfigureAwait(false);
        if (jsonl is null)
        {
            return null;
        }

        var lite = JsonlToLite(jsonl, MtimeFromJsonlTail(jsonl));
        var projectPath = SessionPaths.CanonicalizePath(directory ?? ".");
        return SessionLiteReader.ParseSessionInfoFromLite(sessionId, lite, projectPath);
    }

    public static async Task<IReadOnlyList<SessionMessage>> GetSessionMessagesFromStoreAsync(
        ISessionStore store, string sessionId, string? directory, int? limit, int offset, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);
        var entries = await store.LoadAsync(new SessionKey(projectKey, sessionId), cancellationToken).ConfigureAwait(false);
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        return EntriesToSessionMessages(FilterTranscriptEntries(entries), limit, offset);
    }

    public static async Task<IReadOnlyList<string>> ListSubagentsFromStoreAsync(
        ISessionStore store, string sessionId, string? directory, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        if (store is not ISessionStoreSubkeys subkeys)
        {
            throw new ArgumentException("session_store does not implement list_subkeys() -- cannot list subagents. Provide a store with a list_subkeys() method.");
        }

        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);
        var subpaths = await subkeys.ListSubkeysAsync(new SessionListSubkeysKey(projectKey, sessionId), cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var subpath in subpaths)
        {
            if (!subpath.StartsWith("subagents/", StringComparison.Ordinal))
            {
                continue;
            }

            var last = subpath[(subpath.LastIndexOf('/') + 1)..];
            if (!last.StartsWith("agent-", StringComparison.Ordinal))
            {
                continue;
            }

            var agentId = last["agent-".Length..];
            if (seen.Add(agentId))
            {
                ids.Add(agentId);
            }
        }

        return ids;
    }

    public static async Task<IReadOnlyList<SessionMessage>> GetSubagentMessagesFromStoreAsync(
        ISessionStore store, string sessionId, string agentId, string? directory, int? limit, int offset, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        if (string.IsNullOrEmpty(agentId))
        {
            return [];
        }

        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);

        var subpath = $"subagents/agent-{agentId}";
        if (store is ISessionStoreSubkeys subkeys)
        {
            var subkeyList = await subkeys.ListSubkeysAsync(new SessionListSubkeysKey(projectKey, sessionId), cancellationToken).ConfigureAwait(false);
            var target = $"agent-{agentId}";
            var match = subkeyList.FirstOrDefault(sk => sk.StartsWith("subagents/", StringComparison.Ordinal) && sk[(sk.LastIndexOf('/') + 1)..] == target);
            if (match is null)
            {
                return [];
            }

            subpath = match;
        }

        var entries = await store.LoadAsync(new SessionKey(projectKey, sessionId, subpath), cancellationToken).ConfigureAwait(false);
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        // The synthetic agent_metadata entry (the store's copy of the .meta.json sidecar) records
        // which Agent tool_use spawned this subagent. Recover the parent ids from it -- last one wins
        // -- then drop it: it is not a transcript line.
        var (metaEntry, transcript) = TranscriptSupport.SplitAgentMetadata(entries);
        if (transcript.Count == 0)
        {
            return [];
        }

        var (parentToolUseId, parentAgentId) = TranscriptSupport.ParentIdsFromAgentMetadata(metaEntry);
        return SubagentSessions.EntriesToSubagentMessages(FilterTranscriptEntries(transcript), limit, offset, parentToolUseId, parentAgentId);
    }

    // ---------------------------------------------------------------------
    // list_sessions_from_store fast path (summaries) + gap-fill
    // ---------------------------------------------------------------------

    private sealed class Slot(long mtime, string sessionId, SdkSessionInfo? info)
    {
        public long Mtime { get; } = mtime;

        public string SessionId { get; } = sessionId;

        public SdkSessionInfo? Info { get; set; } = info;
    }

    private static async Task<IReadOnlyList<SdkSessionInfo>> ListFromSummariesAsync(
        ISessionStore store,
        bool hasListSessions,
        IReadOnlyList<SessionSummaryEntry> summaryList,
        string projectKey,
        string projectPath,
        string? directory,
        int? limit,
        int offset,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var knownMtimes = new Dictionary<string, long>(StringComparer.Ordinal);
        var listing = new List<SessionStoreListEntry>();
        if (hasListSessions)
        {
            listing.AddRange(await ((ISessionStoreListing)store).ListSessionsAsync(projectKey, cancellationToken).ConfigureAwait(false));
            foreach (var entry in listing)
            {
                knownMtimes[entry.SessionId] = entry.Mtime;
            }
        }
        else
        {
            SdkLog.SessionSummariesWithoutListSessions(logger ?? NullLogger.Instance);
        }

        // Build a unified slot list. Fresh summaries (mtime >= the session's current mtime from
        // list_sessions) get their info up front; sessions present in list_sessions() but missing OR
        // with a stale sidecar get a placeholder slot routed through the same gap-fill path.
        var slots = new List<Slot>();
        var freshSummaryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var summary in summaryList)
        {
            if (hasListSessions)
            {
                if (!knownMtimes.TryGetValue(summary.SessionId, out var known))
                {
                    // Summary for a session list_sessions() no longer reports -- drop it.
                    continue;
                }

                if (summary.Mtime < known)
                {
                    // Stale sidecar -- let gap-fill re-fold from source.
                    continue;
                }
            }

            var info = SessionSummary.SummaryEntryToSdkInfo(summary, projectPath);
            if (info is null)
            {
                freshSummaryIds.Add(summary.SessionId);
                continue;
            }

            slots.Add(new Slot(summary.Mtime, summary.SessionId, info));
            freshSummaryIds.Add(summary.SessionId);
        }

        if (hasListSessions)
        {
            foreach (var entry in listing)
            {
                if (!freshSummaryIds.Contains(entry.SessionId))
                {
                    slots.Add(new Slot(entry.Mtime, entry.SessionId, null));
                }
            }
        }

        // Paginate BEFORE per-session load so gap-fill load() count is bounded by page size, not total
        // missing.
        slots.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));
        var paged = offset > 0 ? slots.Skip(offset) : slots.AsEnumerable();
        if (limit is > 0)
        {
            paged = paged.Take(limit.Value);
        }

        var page = paged.ToList();

        var toFill = page.Where(slot => slot.Info is null).ToList();
        if (toFill.Count > 0)
        {
            var filled = await DeriveInfosViaLoadAsync(
                store,
                [.. toFill.Select(slot => new SessionStoreListEntry(slot.SessionId, slot.Mtime))],
                directory,
                projectPath,
                cancellationToken).ConfigureAwait(false);
            var bySessionId = filled.ToDictionary(f => f.SessionId, StringComparer.Ordinal);
            foreach (var slot in toFill)
            {
                slot.Info = bySessionId.GetValueOrDefault(slot.SessionId);
            }
        }

        // Gap-fill placeholders that resolved to null (sidechain / no extractable summary after load)
        // are dropped here, AFTER pagination -- that case alone can short-page.
        return [.. page.Where(slot => slot.Info is not null).Select(slot => slot.Info!)];
    }

    // Derives SdkSessionInfo for each listing entry via per-session store.load() + lite-parse. Loads
    // run concurrently with a fixed bound; adapter errors degrade that row to an empty summary instead
    // of failing the whole list. Sidechain and no-summary sessions are dropped.
    private static async Task<List<SdkSessionInfo>> DeriveInfosViaLoadAsync(
        ISessionStore store, List<SessionStoreListEntry> listing, string? directory, string projectPath, CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(StoreListLoadConcurrency);
        var settled = new (string? Jsonl, Exception? Error)[listing.Count];

        async Task BoundedLoadAsync(int index, string sessionId)
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                settled[index] = (await LoadStoreEntriesAsJsonlAsync(store, sessionId, directory, cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception e)
            {
                // Adapter is user code.
                settled[index] = (null, e);
            }
            finally
            {
                limiter.Release();
            }
        }

        await Task.WhenAll(listing.Select((entry, index) => BoundedLoadAsync(index, entry.SessionId))).ConfigureAwait(false);

        var results = new List<SdkSessionInfo>();
        for (var i = 0; i < listing.Count; i++)
        {
            var entry = listing[i];
            var (jsonl, error) = settled[i];
            if (error is not null)
            {
                results.Add(new SdkSessionInfo(entry.SessionId, string.Empty, entry.Mtime));
                continue;
            }

            if (jsonl is null)
            {
                continue;
            }

            var parsed = SessionLiteReader.ParseSessionInfoFromLite(entry.SessionId, JsonlToLite(jsonl, entry.Mtime), projectPath);
            if (parsed is null)
            {
                // Sidechain or no extractable summary -- drop, matching the filesystem path.
                continue;
            }

            results.Add(parsed with { LastModified = entry.Mtime });
        }

        return results;
    }

    private static async Task<string?> LoadStoreEntriesAsJsonlAsync(ISessionStore store, string sessionId, string? directory, CancellationToken cancellationToken)
    {
        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);
        var entries = await store.LoadAsync(new SessionKey(projectKey, sessionId), cancellationToken).ConfigureAwait(false);
        return entries is null || entries.Count == 0 ? null : EntriesToJsonl(entries);
    }

    // Serialize store entries to a JSONL string (one compact JSON object per line). The
    // ISessionStore.LoadAsync contract permits adapters to reorder object keys (e.g. Postgres JSONB),
    // but ParseSessionInfoFromLite scans for `{"type":"tag"` as a line prefix -- hoist "type" to the
    // front so the store path matches the byte shape the disk path produces.
    private static string EntriesToJsonl(IReadOnlyList<JsonObject> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.Append(TypeFirst(entry).ToJsonString());
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static JsonObject TypeFirst(JsonObject entry)
    {
        if (!entry.ContainsKey("type"))
        {
            return (JsonObject)entry.DeepClone();
        }

        var result = new JsonObject { ["type"] = JsonHelpers.GetNode(entry, "type")?.DeepClone() };
        foreach (var (key, value) in entry)
        {
            if (key == "type")
            {
                continue;
            }

            result[key] = value?.DeepClone();
        }

        return result;
    }

    // Builds the head/tail/size lite shape from an in-memory JSONL string. Matches
    // SessionLiteReader.ReadSessionLite's byte semantics so the store path exposes the same slice to
    // ParseSessionInfoFromLite as the disk path would for the same transcript.
    private static LiteSessionFile JsonlToLite(string jsonl, long mtime)
    {
        var buf = Encoding.UTF8.GetBytes(jsonl);
        var size = buf.Length;
        var headLen = Math.Min(size, SessionLiteReader.LiteReadBufSize);
        var head = Encoding.UTF8.GetString(buf, 0, headLen);
        var tail = size > SessionLiteReader.LiteReadBufSize
            ? Encoding.UTF8.GetString(buf, Math.Max(0, size - SessionLiteReader.LiteReadBufSize), Math.Min(size, SessionLiteReader.LiteReadBufSize))
            : head;
        return new LiteSessionFile { Mtime = mtime, Size = size, Head = head, Tail = tail };
    }

    // Best-effort mtime: parse the last entry's "timestamp" field. Falls back to the current
    // wall-clock time when absent or unparseable.
    private static long MtimeFromJsonlTail(string jsonl)
    {
        var trimmed = jsonl.TrimEnd();
        var lastLine = trimmed[(trimmed.LastIndexOf('\n') + 1)..];
        try
        {
            if (JsonNode.Parse(lastLine) is JsonObject obj)
            {
                var ts = JsonHelpers.GetString(obj, "timestamp");
                if (!string.IsNullOrEmpty(ts))
                {
                    var norm = ts.EndsWith('Z') ? ts.Replace("Z", "+00:00") : ts;
                    if (DateTimeOffset.TryParse(norm, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    {
                        return parsed.ToUnixTimeMilliseconds();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // Filters store-loaded entries to transcript message types with a uuid. Mirrors
    // TranscriptSupport.ParseTranscriptEntries for the already-parsed object path so chain-building
    // never sees metadata-only entries (custom-title, tag, agent_metadata, etc.).
    private static List<JsonObject> FilterTranscriptEntries(IReadOnlyList<JsonObject> entries)
    {
        var result = new List<JsonObject>();
        foreach (var entry in entries)
        {
            var type = JsonHelpers.GetString(entry, "type");
            if (type is not null && TranscriptSupport.TranscriptEntryTypes.Contains(type) && JsonHelpers.GetString(entry, "uuid") is not null)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static List<SessionMessage> EntriesToSessionMessages(List<JsonObject> entries, int? limit, int offset)
    {
        var chain = SessionsCore.BuildConversationChain(entries);
        var visible = chain.Where(SessionsCore.IsVisibleMessage).ToList();
        var messages = visible.Select(e => SessionsCore.ToSessionMessage(e)).ToList();

        if (limit is > 0)
        {
            return [.. messages.Skip(offset).Take(limit.Value)];
        }

        if (offset > 0)
        {
            return [.. messages.Skip(offset)];
        }

        return messages;
    }

    private static List<SdkSessionInfo> ApplySortLimitOffset(List<SdkSessionInfo> sessions, int? limit, int offset)
    {
        IEnumerable<SdkSessionInfo> result = sessions.OrderByDescending(s => s.LastModified);
        if (offset > 0)
        {
            result = result.Skip(offset);
        }

        if (limit is > 0)
        {
            result = result.Take(limit.Value);
        }

        return [.. result];
    }
}
