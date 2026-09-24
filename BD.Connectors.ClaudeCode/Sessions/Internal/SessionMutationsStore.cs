using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// RenameSessionViaStoreAsync / TagSessionViaStoreAsync / DeleteSessionViaStoreAsync /
// ForkSessionViaStoreAsync -- async, store-backed counterparts to the local (disk) mutations in
// SessionMutationsCore. Ported from PY.
internal static class SessionMutationsStore
{
    public static async Task RenameSessionViaStoreAsync(ISessionStore store, string sessionId, string title, string? directory, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        var stripped = title.Trim();
        if (stripped.Length == 0)
        {
            throw new ArgumentException("title must be non-empty");
        }

        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);
        var entry = new JsonObject
        {
            ["type"] = "custom-title",
            ["customTitle"] = stripped,
            ["sessionId"] = sessionId,
            ["uuid"] = Guid.NewGuid().ToString(),
            ["timestamp"] = SessionMutationsCore.FormatIsoNow(),
        };
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [entry], cancellationToken).ConfigureAwait(false);
    }

    public static async Task TagSessionViaStoreAsync(ISessionStore store, string sessionId, string? tag, string? directory, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        var finalTag = tag;
        if (tag is not null)
        {
            var sanitized = SessionMutationsCore.SanitizeUnicode(tag).Trim();
            if (sanitized.Length == 0)
            {
                throw new ArgumentException("tag must be non-empty (use None to clear)");
            }

            finalTag = sanitized;
        }

        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);
        var entry = new JsonObject
        {
            ["type"] = "tag",
            ["tag"] = finalTag ?? string.Empty,
            ["sessionId"] = sessionId,
            ["uuid"] = Guid.NewGuid().ToString(),
            ["timestamp"] = SessionMutationsCore.FormatIsoNow(),
        };
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [entry], cancellationToken).ConfigureAwait(false);
    }

    // If the store does not implement ISessionStoreDeletion, deletion is a no-op (appropriate for
    // WORM/append-only backends -- matches the ISessionStore contract).
    public static async Task DeleteSessionViaStoreAsync(ISessionStore store, string sessionId, string? directory, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        if (store is not ISessionStoreDeletion deletion)
        {
            return;
        }

        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);
        await deletion.DeleteAsync(new SessionKey(projectKey, sessionId), cancellationToken).ConfigureAwait(false);
    }

    // Runs the fork transform directly over the objects returned by store.LoadAsync() -- no JSONL
    // round-trip. A storage-layer copy (e.g. S3 CopyObject) is NOT sufficient: the transform remaps
    // every UUID, rewrites sessionId on each entry, and stamps forkedFrom, so the data must pass
    // through this process once.
    public static async Task<ForkSessionResult> ForkSessionViaStoreAsync(
        ISessionStore store, string sessionId, string? directory, string? upToMessageId, string? title, CancellationToken cancellationToken)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        if (!string.IsNullOrEmpty(upToMessageId) && SessionPaths.ValidateUuid(upToMessageId) is null)
        {
            throw new ArgumentException($"Invalid up_to_message_id: {upToMessageId}");
        }

        var projectKey = ClaudeSessions.ProjectKeyForDirectory(directory);
        var srcKey = new SessionKey(projectKey, sessionId);
        var loaded = await store.LoadAsync(srcKey, cancellationToken).ConfigureAwait(false);
        if (loaded is null || loaded.Count == 0)
        {
            throw new FileNotFoundException($"Session {sessionId} not found");
        }

        // Partition into transcript entries (with uuid) and content-replacement records, mirroring the
        // disk path's ParseForkTranscript for the already-parsed path.
        var transcript = new List<JsonObject>();
        var contentReplacements = new List<JsonNode?>();
        foreach (var entry in loaded)
        {
            var entryType = JsonHelpers.GetString(entry, "type");
            if (entryType is not null && SessionMutationsCore._transcriptTypes.Contains(entryType) && JsonHelpers.GetString(entry, "uuid") is not null)
            {
                transcript.Add(entry);
            }
            else if (entryType == "content-replacement"
                     && JsonHelpers.GetString(entry, "sessionId") == sessionId
                     && JsonHelpers.GetArray(entry, "replacements") is { } replacements)
            {
                contentReplacements.AddRange(replacements);
            }
        }

        // Mirrors the disk path's head/tail title scan, but over parsed entry objects: last occurrence
        // wins for both customTitle and aiTitle; customTitle beats aiTitle; first user prompt is the
        // final fallback (reusing ExtractFirstPromptFromHead over a re-serialized JSONL string so
        // skip-patterns/truncation match the disk path exactly).
        string? DeriveTitle()
        {
            string? custom = null;
            string? ai = null;
            foreach (var entry in loaded)
            {
                var customTitle = JsonHelpers.GetString(entry, "customTitle");
                if (!string.IsNullOrEmpty(customTitle))
                {
                    custom = customTitle;
                }

                var aiTitle = JsonHelpers.GetString(entry, "aiTitle");
                if (!string.IsNullOrEmpty(aiTitle))
                {
                    ai = aiTitle;
                }
            }

            if (custom is not null)
            {
                return custom;
            }

            if (ai is not null)
            {
                return ai;
            }

            var jsonl = string.Join('\n', loaded.Select(e => e.ToJsonString())) + "\n";
            var firstPrompt = SessionLiteReader.ExtractFirstPromptFromHead(jsonl);
            return firstPrompt.Length > 0 ? firstPrompt : null;
        }

        var (forkedSessionId, lines) = SessionMutationsCore.BuildForkLines(transcript, contentReplacements, sessionId, upToMessageId, title, DeriveTitle);

        // BuildForkLines emits compact JSON strings; re-parse to objects so the store receives the same
        // shape it would from the mirror path.
        var entries = lines.Select(line => (JsonObject)JsonNode.Parse(line)!).ToList();
        await store.AppendAsync(new SessionKey(projectKey, forkedSessionId), entries, cancellationToken).ConfigureAwait(false);
        return new ForkSessionResult(forkedSessionId);
    }
}
