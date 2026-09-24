using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// ListSubagentsAsync / GetSubagentMessagesAsync. Ported from PY.
internal static class SubagentSessions
{
    public static List<string> ListSubagents(string sessionId, string? directory)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        var subagentsDir = ResolveSubagentsDir(sessionId, directory);
        if (subagentsDir is null)
        {
            return [];
        }

        return [.. CollectAgentFiles(subagentsDir).Select(x => x.AgentId)];
    }

    public static List<SessionMessage> GetSubagentMessages(string sessionId, string agentId, string? directory, int? limit, int offset)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        if (string.IsNullOrEmpty(agentId))
        {
            return [];
        }

        var subagentsDir = ResolveSubagentsDir(sessionId, directory);
        if (subagentsDir is null)
        {
            return [];
        }

        // The agent file may be directly in subagents/ or in a nested subdirectory — scan for it.
        string? match = null;
        foreach (var (foundId, filePath) in CollectAgentFiles(subagentsDir))
        {
            if (foundId == agentId)
            {
                match = filePath;
                break;
            }
        }

        if (match is null)
        {
            return [];
        }

        string content;
        try
        {
            content = File.ReadAllText(match, Encoding.UTF8);
        }
        catch (Exception)
        {
            return [];
        }

        if (content.Length == 0)
        {
            return [];
        }

        JsonObject? meta;
        try
        {
            meta = ReadAgentMetadataSidecar(match);
        }
        catch (Exception)
        {
            meta = null;
        }

        var (parentToolUseId, parentAgentId) = TranscriptSupport.ParentIdsFromAgentMetadata(meta);

        var entries = TranscriptSupport.ParseTranscriptEntries(content);
        return EntriesToSubagentMessages(entries, limit, offset, parentToolUseId, parentAgentId);
    }

    // Resolves the on-disk path of a session JSONL file. Directory resolution mirrors
    // SessionsCore.ReadSessionFile: when `directory` is provided, looks in that project directory and
    // its git worktrees; otherwise searches all project directories. Returns the first non-empty
    // match, or null if not found (matches PY).
    internal static string? ResolveSessionFilePath(string sessionId, string? directory)
    {
        var fileName = $"{sessionId}.jsonl";

        static string? StatCandidate(string projectDir, string fileName)
        {
            var candidate = Path.Combine(projectDir, fileName);
            try
            {
                var info = new FileInfo(candidate);
                if (info.Exists && info.Length > 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Best-effort stat, matches PY's `except OSError: pass`.
            }

            return null;
        }

        if (!string.IsNullOrEmpty(directory))
        {
            var canonicalDir = SessionPaths.CanonicalizePath(directory);

            var projectDir = SessionPaths.FindProjectDir(canonicalDir);
            if (projectDir is not null)
            {
                var found = StatCandidate(projectDir, fileName);
                if (found is not null)
                {
                    return found;
                }
            }

            List<string> worktreePaths;
            try
            {
                worktreePaths = [.. SessionPaths.GetWorktreePaths(canonicalDir)];
            }
            catch (Exception)
            {
                worktreePaths = [];
            }

            foreach (var wt in worktreePaths)
            {
                if (wt == canonicalDir)
                {
                    continue;
                }

                var wtProjectDir = SessionPaths.FindProjectDir(wt);
                if (wtProjectDir is not null)
                {
                    var found = StatCandidate(wtProjectDir, fileName);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        var projectsDir = SessionPaths.GetProjectsDir();
        List<string> dirents;
        try
        {
            dirents = [.. Directory.EnumerateDirectories(projectsDir)];
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var entry in dirents)
        {
            var found = StatCandidate(entry, fileName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // The session file lives at <projectDir>/<sessionId>.jsonl and the subagents directory at
    // <projectDir>/<sessionId>/subagents/.
    private static string? ResolveSubagentsDir(string sessionId, string? directory)
    {
        var resolved = ResolveSessionFilePath(sessionId, directory);
        if (resolved is null)
        {
            return null;
        }

        var sessionDir = resolved[..^".jsonl".Length];
        return Path.Combine(sessionDir, "subagents");
    }

    // Recursively collects agent-*.jsonl files from a directory tree, sorted by name at each level,
    // matching PY. Subagent transcripts may live directly in subagents/ or nested, e.g.
    // subagents/workflows/<runId>/.
    private static List<(string AgentId, string Path)> CollectAgentFiles(string baseDir)
    {
        var results = new List<(string, string)>();

        void Walk(string currentDir)
        {
            List<string> dirents;
            try
            {
                dirents = [.. Directory.EnumerateFileSystemEntries(currentDir).OrderBy(Path.GetFileName, StringComparer.Ordinal)];
            }
            catch (Exception)
            {
                return;
            }

            foreach (var entry in dirents)
            {
                var name = Path.GetFileName(entry);
                if (File.Exists(entry) && name.StartsWith("agent-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal))
                {
                    var agentId = name["agent-".Length..^".jsonl".Length];
                    results.Add((agentId, entry));
                }
                else if (Directory.Exists(entry))
                {
                    Walk(entry);
                }
            }
        }

        Walk(baseDir);
        return results;
    }

    // agent-<id>.jsonl -> agent-<id>.meta.json (same directory).
    internal static string AgentMetadataSidecarPath(string transcriptPath) => transcriptPath[..^".jsonl".Length] + ".meta.json";

    // Reads the .meta.json sidecar beside a subagent transcript. Returns null on any failure (missing,
    // unreadable, corrupt JSON, not an object) — an unusable optional sidecar degrades to an absent
    // one, matching what the get_subagent_messages call site observes in PY (it wraps this in its own
    // `except OSError` too, so every failure mode already ends in "no metadata").
    internal static JsonObject? ReadAgentMetadataSidecar(string transcriptPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(AgentMetadataSidecarPath(transcriptPath), Encoding.UTF8);
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Subagent transcripts are simpler than main sessions — no compaction, no sidechains, no
    // preserved segments. Find the last user/assistant entry and walk parentUuid links back to the
    // root (matches PY).
    internal static List<JsonObject> BuildSubagentChain(List<JsonObject> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var byUuid = new Dictionary<string, JsonObject>();
        foreach (var entry in entries)
        {
            byUuid[TranscriptSupport.GetUuid(entry)] = entry;
        }

        JsonObject? leaf = null;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var type = JsonHelpers.GetString(entries[i], "type");
            if (type is "user" or "assistant")
            {
                leaf = entries[i];
                break;
            }
        }

        if (leaf is null)
        {
            return [];
        }

        var chain = new List<JsonObject>();
        var seen = new HashSet<string>();
        var current = leaf;
        while (current is not null)
        {
            var uid = TranscriptSupport.GetUuid(current);
            if (!seen.Add(uid))
            {
                break;
            }

            chain.Add(current);
            var parent = JsonHelpers.GetString(current, "parentUuid");
            current = !string.IsNullOrEmpty(parent) && byUuid.TryGetValue(parent, out var p) ? p : null;
        }

        chain.Reverse();
        return chain;
    }

    // Every message in a subagent transcript shares the same parent ids (matches PY).
    internal static List<SessionMessage> EntriesToSubagentMessages(
        List<JsonObject> entries, int? limit, int offset, string? parentToolUseId, string? parentAgentId)
    {
        var chain = BuildSubagentChain(entries);
        var messages = chain
            .Where(e => JsonHelpers.GetString(e, "type") is "user" or "assistant")
            .Select(e => SessionsCore.ToSessionMessage(e, parentToolUseId, parentAgentId))
            .ToList();

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
}
