using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// ListSessionsAsync / GetSessionInfoAsync / GetSessionMessagesAsync. Ported from PY.
internal static class SessionsCore
{
    // ---------------------------------------------------------------------
    // list_sessions (F6.3)
    // ---------------------------------------------------------------------

    public static List<SdkSessionInfo> ListSessions(string? directory, int? limit, int offset, bool includeWorktrees)
    {
        return !string.IsNullOrEmpty(directory)
            ? ListSessionsForProject(directory, limit, offset, includeWorktrees)
            : ListAllSessions(limit, offset);
    }

    private static List<SdkSessionInfo> ReadSessionsFromDir(string projectDir, string? projectPath = null)
    {
        List<string> entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(projectDir)];
        }
        catch (Exception)
        {
            return [];
        }

        var results = new List<SdkSessionInfo>();
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!name.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                continue;
            }

            var sessionId = SessionPaths.ValidateUuid(name[..^".jsonl".Length]);
            if (sessionId is null)
            {
                continue;
            }

            var lite = SessionLiteReader.ReadSessionLite(entry);
            if (lite is null)
            {
                continue;
            }

            var info = SessionLiteReader.ParseSessionInfoFromLite(sessionId, lite, projectPath);
            if (info is not null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    private static List<SdkSessionInfo> DeduplicateBySessionId(List<SdkSessionInfo> sessions)
    {
        var byId = new Dictionary<string, SdkSessionInfo>();
        foreach (var s in sessions)
        {
            if (!byId.TryGetValue(s.SessionId, out var existing) || s.LastModified > existing.LastModified)
            {
                byId[s.SessionId] = s;
            }
        }

        return [.. byId.Values];
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

    private static List<SdkSessionInfo> ListSessionsForProject(string directory, int? limit, int offset, bool includeWorktrees)
    {
        var canonicalDir = SessionPaths.CanonicalizePath(directory);

        List<string> worktreePaths;
        if (includeWorktrees)
        {
            try
            {
                worktreePaths = [.. SessionPaths.GetWorktreePaths(canonicalDir)];
            }
            catch (Exception)
            {
                worktreePaths = [];
            }
        }
        else
        {
            worktreePaths = [];
        }

        if (worktreePaths.Count <= 1)
        {
            var projectDir = SessionPaths.FindProjectDir(canonicalDir);
            if (projectDir is null)
            {
                return [];
            }

            return ApplySortLimitOffset(ReadSessionsFromDir(projectDir, canonicalDir), limit, offset);
        }

        var projectsDir = SessionPaths.GetProjectsDir();
        var caseInsensitive = OperatingSystem.IsWindows();

        // Sort worktree paths by sanitized prefix length (longest first) so more specific matches
        // take priority over shorter ones.
        var indexed = worktreePaths
            .Select(wt =>
            {
                var sanitized = SessionPaths.SanitizePath(wt);
                var prefix = caseInsensitive ? sanitized.ToLowerInvariant() : sanitized;
                return (Wt: wt, Prefix: prefix);
            })
            .OrderByDescending(x => x.Prefix.Length)
            .ToList();

        List<string> allDirents;
        try
        {
            allDirents = [.. Directory.EnumerateDirectories(projectsDir)];
        }
        catch (Exception)
        {
            var projectDir = SessionPaths.FindProjectDir(canonicalDir);
            if (projectDir is null)
            {
                return ApplySortLimitOffset([], limit, offset);
            }

            return ApplySortLimitOffset(ReadSessionsFromDir(projectDir, canonicalDir), limit, offset);
        }

        var allSessions = new List<SdkSessionInfo>();
        var seenDirs = new HashSet<string>();

        // Always include the user's actual directory (handles subdirectories that won't match
        // worktree root prefixes).
        var canonicalProjectDir = SessionPaths.FindProjectDir(canonicalDir);
        if (canonicalProjectDir is not null)
        {
            var dirBase = Path.GetFileName(canonicalProjectDir);
            seenDirs.Add(caseInsensitive ? dirBase.ToLowerInvariant() : dirBase);
            allSessions.AddRange(ReadSessionsFromDir(canonicalProjectDir, canonicalDir));
        }

        foreach (var entry in allDirents)
        {
            var rawName = Path.GetFileName(entry);
            var dirName = caseInsensitive ? rawName.ToLowerInvariant() : rawName;
            if (seenDirs.Contains(dirName))
            {
                continue;
            }

            foreach (var (wt, prefix) in indexed)
            {
                // Only use startswith for truncated paths (>MaxSanitizedLength) where a hash suffix
                // follows. For short paths, require exact match to avoid /root/project matching
                // /root/project-foo.
                var isMatch = dirName == prefix
                    || (prefix.Length >= SessionPaths.MaxSanitizedLength && dirName.StartsWith(prefix + "-", StringComparison.Ordinal));
                if (isMatch)
                {
                    seenDirs.Add(dirName);
                    allSessions.AddRange(ReadSessionsFromDir(entry, wt));
                    break;
                }
            }
        }

        return ApplySortLimitOffset(DeduplicateBySessionId(allSessions), limit, offset);
    }

    private static List<SdkSessionInfo> ListAllSessions(int? limit, int offset)
    {
        var projectsDir = SessionPaths.GetProjectsDir();
        List<string> projectDirs;
        try
        {
            projectDirs = [.. Directory.EnumerateDirectories(projectsDir)];
        }
        catch (Exception)
        {
            return [];
        }

        var allSessions = new List<SdkSessionInfo>();
        foreach (var projectDir in projectDirs)
        {
            allSessions.AddRange(ReadSessionsFromDir(projectDir));
        }

        return ApplySortLimitOffset(DeduplicateBySessionId(allSessions), limit, offset);
    }

    // ---------------------------------------------------------------------
    // get_session_info (F6.4)
    // ---------------------------------------------------------------------

    public static SdkSessionInfo? GetSessionInfo(string sessionId, string? directory)
    {
        var uuid = SessionPaths.ValidateUuid(sessionId);
        if (uuid is null)
        {
            return null;
        }

        var fileName = $"{uuid}.jsonl";

        if (!string.IsNullOrEmpty(directory))
        {
            var canonical = SessionPaths.CanonicalizePath(directory);
            var projectDir = SessionPaths.FindProjectDir(canonical);
            if (projectDir is not null)
            {
                var lite = SessionLiteReader.ReadSessionLite(Path.Combine(projectDir, fileName));
                if (lite is not null)
                {
                    return SessionLiteReader.ParseSessionInfoFromLite(uuid, lite, canonical);
                }
            }

            List<string> worktreePaths;
            try
            {
                worktreePaths = [.. SessionPaths.GetWorktreePaths(canonical)];
            }
            catch (Exception)
            {
                worktreePaths = [];
            }

            foreach (var wt in worktreePaths)
            {
                if (wt == canonical)
                {
                    continue;
                }

                var wtProjectDir = SessionPaths.FindProjectDir(wt);
                if (wtProjectDir is not null)
                {
                    var lite = SessionLiteReader.ReadSessionLite(Path.Combine(wtProjectDir, fileName));
                    if (lite is not null)
                    {
                        return SessionLiteReader.ParseSessionInfoFromLite(uuid, lite, wt);
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
            var lite = SessionLiteReader.ReadSessionLite(Path.Combine(entry, fileName));
            if (lite is not null)
            {
                return SessionLiteReader.ParseSessionInfoFromLite(uuid, lite);
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // get_session_messages (F6.4)
    // ---------------------------------------------------------------------

    public static List<SessionMessage> GetSessionMessages(string sessionId, string? directory, int? limit, int offset)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        var content = ReadSessionFile(sessionId, directory);
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var entries = TranscriptSupport.ParseTranscriptEntries(content);
        return EntriesToSessionMessages(entries, limit, offset);
    }

    private static string? TryReadSessionFile(string projectDir, string fileName)
    {
        try
        {
            return File.ReadAllText(Path.Combine(projectDir, fileName), System.Text.Encoding.UTF8);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadSessionFile(string sessionId, string? directory)
    {
        var fileName = $"{sessionId}.jsonl";

        if (!string.IsNullOrEmpty(directory))
        {
            var canonicalDir = SessionPaths.CanonicalizePath(directory);

            var projectDir = SessionPaths.FindProjectDir(canonicalDir);
            if (projectDir is not null)
            {
                var content = TryReadSessionFile(projectDir, fileName);
                if (!string.IsNullOrEmpty(content))
                {
                    return content;
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
                    var content = TryReadSessionFile(wtProjectDir, fileName);
                    if (!string.IsNullOrEmpty(content))
                    {
                        return content;
                    }
                }
            }

            return null;
        }

        var projectsDir = SessionPaths.GetProjectsDir();
        List<string> dirents;
        try
        {
            dirents = [.. Directory.EnumerateFileSystemEntries(projectsDir)];
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var entry in dirents)
        {
            var content = TryReadSessionFile(entry, fileName);
            if (!string.IsNullOrEmpty(content))
            {
                return content;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Conversation chain building — shared internal helpers (also used by SubagentSessions).
    // ---------------------------------------------------------------------

    internal static List<JsonObject> BuildConversationChain(List<JsonObject> entries)
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

        var entryIndex = new Dictionary<string, int>();
        for (var i = 0; i < entries.Count; i++)
        {
            entryIndex[TranscriptSupport.GetUuid(entries[i])] = i;
        }

        var parentUuids = new HashSet<string>();
        foreach (var entry in entries)
        {
            var parent = JsonHelpers.GetString(entry, "parentUuid");
            if (!string.IsNullOrEmpty(parent))
            {
                parentUuids.Add(parent);
            }
        }

        var terminals = entries.Where(e => !parentUuids.Contains(TranscriptSupport.GetUuid(e))).ToList();

        var leaves = new List<JsonObject>();
        foreach (var terminal in terminals)
        {
            var cur = terminal;
            var seen = new HashSet<string>();
            while (cur is not null)
            {
                var uid = TranscriptSupport.GetUuid(cur);
                if (!seen.Add(uid))
                {
                    break;
                }

                var type = JsonHelpers.GetString(cur, "type");
                if (type is "user" or "assistant")
                {
                    leaves.Add(cur);
                    break;
                }

                var parent = JsonHelpers.GetString(cur, "parentUuid");
                cur = !string.IsNullOrEmpty(parent) && byUuid.TryGetValue(parent, out var p) ? p : null;
            }
        }

        if (leaves.Count == 0)
        {
            return [];
        }

        // Pick the leaf from the main chain (not sidechain/team/meta), preferring the highest
        // position in the entries array (most recent in file).
        var mainLeaves = leaves.Where(l =>
            JsonHelpers.GetBool(l, "isSidechain") != true
            && string.IsNullOrEmpty(JsonHelpers.GetString(l, "teamName"))
            && JsonHelpers.GetBool(l, "isMeta") != true).ToList();

        JsonObject PickBest(List<JsonObject> candidates)
        {
            var best = candidates[0];
            var bestIdx = entryIndex.GetValueOrDefault(TranscriptSupport.GetUuid(best), -1);
            foreach (var cur in candidates.Skip(1))
            {
                var curIdx = entryIndex.GetValueOrDefault(TranscriptSupport.GetUuid(cur), -1);
                if (curIdx > bestIdx)
                {
                    best = cur;
                    bestIdx = curIdx;
                }
            }

            return best;
        }

        var leaf = mainLeaves.Count > 0 ? PickBest(mainLeaves) : PickBest(leaves);

        var chain = new List<JsonObject>();
        var chainSeen = new HashSet<string>();
        var chainCur = leaf;
        while (chainCur is not null)
        {
            var uid = TranscriptSupport.GetUuid(chainCur);
            if (!chainSeen.Add(uid))
            {
                break;
            }

            chain.Add(chainCur);
            var parent = JsonHelpers.GetString(chainCur, "parentUuid");
            chainCur = !string.IsNullOrEmpty(parent) && byUuid.TryGetValue(parent, out var p) ? p : null;
        }

        chain.Reverse();
        return chain;
    }

    internal static bool IsVisibleMessage(JsonObject entry)
    {
        var type = JsonHelpers.GetString(entry, "type");
        if (type != "user" && type != "assistant")
        {
            return false;
        }

        if (JsonHelpers.GetBool(entry, "isMeta") == true)
        {
            return false;
        }

        if (JsonHelpers.GetBool(entry, "isSidechain") == true)
        {
            return false;
        }

        // isCompactSummary messages are intentionally included, matching PY.
        return string.IsNullOrEmpty(JsonHelpers.GetString(entry, "teamName"));
    }

    internal static SessionMessage ToSessionMessage(JsonObject entry, string? parentToolUseId = null, string? parentAgentId = null)
    {
        var type = JsonHelpers.GetString(entry, "type");
        var msgType = type == "user" ? "user" : "assistant";
        return new SessionMessage(
            msgType,
            JsonHelpers.GetString(entry, "uuid") ?? string.Empty,
            JsonHelpers.GetString(entry, "sessionId") ?? string.Empty,
            JsonHelpers.GetNode(entry, "message"),
            parentToolUseId,
            parentAgentId);
    }

    private static List<SessionMessage> EntriesToSessionMessages(List<JsonObject> entries, int? limit, int offset)
    {
        var chain = BuildConversationChain(entries);
        var visible = chain.Where(IsVisibleMessage).ToList();
        var messages = visible.Select(e => ToSessionMessage(e)).ToList();

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
