using System.Text;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// ImportSessionToStoreAsync -- replay a local on-disk session transcript into an ISessionStore. This
// is the inverse of SessionResume -- where MaterializeResumeSessionAsync reads a store and writes a
// temp ~/.claude tree, ImportSessionToStoreAsync reads the local
// ~/.claude/projects/<dir>/<sessionId>.jsonl (plus subagent transcripts) and replays each line into
// store.AppendAsync(). Mirrors the TypeScript SDK's importSessionToStore. Ported from PY.
internal static class SessionImport
{
    public const int DefaultBatchSize = TranscriptMirrorBatcher.DefaultMaxPendingEntries;

    public static async Task ImportSessionToStoreAsync(
        string sessionId,
        ISessionStore store,
        string? directory = null,
        bool includeSubagents = true,
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        var resolved = SubagentSessions.ResolveSessionFilePath(sessionId, directory)
            ?? throw new FileNotFoundException($"Session {sessionId} not found");

        // Key under the on-disk project directory name -- matches SessionStoreFileKeys/
        // TranscriptMirrorBatcher even when the resolver's search (directory=null) or worktree
        // fallback found the file somewhere other than `directory`.
        var projectKey = Path.GetFileName(Path.GetDirectoryName(resolved)) ?? string.Empty;
        if (batchSize <= 0)
        {
            batchSize = DefaultBatchSize;
        }

        var mainKey = new SessionKey(projectKey, sessionId);
        await AppendJsonlFileInBatchesAsync(resolved, mainKey, store, batchSize, cancellationToken).ConfigureAwait(false);

        if (!includeSubagents)
        {
            return;
        }

        // Subagent transcripts live at <projectDir>/<sessionId>/subagents/**.
        var sessionDir = resolved[..^".jsonl".Length];
        var subagentsDir = Path.Combine(sessionDir, "subagents");
        foreach (var filePath in CollectJsonlFiles(subagentsDir))
        {
            // subpath is the path relative to sessionDir, '/'-joined, sans .jsonl -- e.g.
            // subagents/agent-abc or subagents/workflows/run-1/agent-def. Matches
            // SessionStoreFileKeys.FilePathToSessionKey so ListSubkeysAsync() and
            // GetSubagentMessagesFromStoreAsync() round-trip.
            var relParts = Path.GetRelativePath(sessionDir, filePath).Split(['\\', '/']);
            relParts[^1] = relParts[^1][..^".jsonl".Length];
            var subKey = new SessionKey(projectKey, sessionId, string.Join('/', relParts));
            await AppendJsonlFileInBatchesAsync(filePath, subKey, store, batchSize, cancellationToken).ConfigureAwait(false);

            // The on-disk .jsonl does NOT contain agent_metadata entries -- those are only sent to
            // live mirrors and persisted in the .meta.json sidecar. Import the sidecar so
            // MaterializeResumeSessionAsync() can recreate it and resumed subagents keep their
            // agentType/worktreePath. A missing, corrupt, or non-object sidecar is treated as absent
            // (the transcript is still imported).
            var meta = SubagentSessions.ReadAgentMetadataSidecar(filePath);
            if (meta is not null)
            {
                // Synthetic discriminator last so a stray "type" key in the CLI-owned sidecar can
                // never shadow it.
                var metaEntry = (JsonObject)meta.DeepClone();
                metaEntry["type"] = "agent_metadata";
                await store.AppendAsync(subKey, [metaEntry], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Stream-reads a JSONL file line-by-line, parsing each line and flushing to store.AppendAsync() in
    // batches of batchSize entries (or DefaultMaxPendingBytes of line text, whichever comes first).
    // Skips blank lines.
    private static async Task AppendJsonlFileInBatchesAsync(string filePath, SessionKey key, ISessionStore store, int batchSize, CancellationToken cancellationToken)
    {
        var batch = new List<JsonObject>();
        var nbytes = 0;
        foreach (var line in File.ReadLines(filePath, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }

            batch.Add((JsonObject)JsonNode.Parse(line)!);
            nbytes += line.Length;
            if (batch.Count >= batchSize || nbytes >= TranscriptMirrorBatcher.DefaultMaxPendingBytes)
            {
                await store.AppendAsync(key, batch, cancellationToken).ConfigureAwait(false);
                batch = [];
                nbytes = 0;
            }
        }

        if (batch.Count > 0)
        {
            await store.AppendAsync(key, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    // Recursively collects *.jsonl file paths under baseDir. Returns empty if baseDir does not exist.
    // Sorted per directory so import order is deterministic across platforms.
    private static List<string> CollectJsonlFiles(string baseDir)
    {
        var results = new List<string>();
        List<string> dirents;
        try
        {
            dirents = [.. Directory.EnumerateFileSystemEntries(baseDir).OrderBy(Path.GetFileName, StringComparer.Ordinal)];
        }
        catch (Exception)
        {
            return results;
        }

        foreach (var entry in dirents)
        {
            if (Directory.Exists(entry))
            {
                results.AddRange(CollectJsonlFiles(entry));
            }
            else if (File.Exists(entry) && entry.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                results.Add(entry);
            }
        }

        return results;
    }
}
