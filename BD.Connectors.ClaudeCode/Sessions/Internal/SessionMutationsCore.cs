using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// RenameSessionAsync / TagSessionAsync / DeleteSessionAsync / ForkSessionAsync — the local (disk)
// mutation functions. Ported from PY.
internal static class SessionMutationsCore
{
    private static readonly string[] _strippedForkKeys = ["teamName", "agentName", "slug", "sourceToolAssistantUUID"];

    public static void RenameSession(string sessionId, string title, string? directory)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        // Matches CLI guard — empty/whitespace titles are rejected rather than overloaded as "clear title".
        var stripped = title.Trim();
        if (stripped.Length == 0)
        {
            throw new ArgumentException("title must be non-empty");
        }

        var entry = new JsonObject
        {
            ["type"] = "custom-title",
            ["customTitle"] = stripped,
            ["sessionId"] = sessionId,
        };

        AppendToSession(sessionId, entry.ToJsonString() + "\n", directory);
    }

    public static void TagSession(string sessionId, string? tag, string? directory)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        var finalTag = tag;
        if (tag is not null)
        {
            var sanitized = SanitizeUnicode(tag).Trim();
            if (sanitized.Length == 0)
            {
                throw new ArgumentException("tag must be non-empty (use None to clear)");
            }

            finalTag = sanitized;
        }

        var entry = new JsonObject
        {
            ["type"] = "tag",
            ["tag"] = finalTag ?? string.Empty,
            ["sessionId"] = sessionId,
        };

        AppendToSession(sessionId, entry.ToJsonString() + "\n", directory);
    }

    public static void DeleteSession(string sessionId, string? directory)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        var path = FindSessionFile(sessionId, directory)
            ?? throw new FileNotFoundException(
                $"Session {sessionId} not found" + (!string.IsNullOrEmpty(directory) ? $" in project directory for {directory}" : string.Empty));

        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException e)
        {
            throw new FileNotFoundException($"Session {sessionId} not found", e);
        }

        // Subagent transcripts live in a sibling {session_id}/ dir; often absent.
        var sideDir = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, sessionId);
        try
        {
            if (Directory.Exists(sideDir))
            {
                Directory.Delete(sideDir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cascade delete, matches PY's shutil.rmtree(..., ignore_errors=True).
        }
    }

    public static ForkSessionResult ForkSession(string sessionId, string? directory, string? upToMessageId, string? title)
    {
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}");
        }

        if (!string.IsNullOrEmpty(upToMessageId) && SessionPaths.ValidateUuid(upToMessageId) is null)
        {
            throw new ArgumentException($"Invalid up_to_message_id: {upToMessageId}");
        }

        var source = FindSessionFileWithDir(sessionId, directory)
            ?? throw new FileNotFoundException(
                $"Session {sessionId} not found" + (!string.IsNullOrEmpty(directory) ? $" in project directory for {directory}" : string.Empty));

        var (filePath, projectDir) = source;

        var content = File.ReadAllBytes(filePath);
        if (content.Length == 0)
        {
            throw new InvalidOperationException($"Session {sessionId} has no messages to fork");
        }

        var (transcript, contentReplacements) = ParseForkTranscript(content, sessionId);

        string? DeriveTitle()
        {
            var bufLen = content.Length;
            var head = Encoding.UTF8.GetString(content, 0, Math.Min(bufLen, SessionLiteReader.LiteReadBufSize));
            var tailStart = Math.Max(0, bufLen - SessionLiteReader.LiteReadBufSize);
            var tail = Encoding.UTF8.GetString(content, tailStart, bufLen - tailStart);
            var firstPrompt = SessionLiteReader.ExtractFirstPromptFromHead(head);
            return SessionLiteReader.ExtractLastJsonStringField(tail, "customTitle")
                ?? SessionLiteReader.ExtractLastJsonStringField(head, "customTitle")
                ?? SessionLiteReader.ExtractLastJsonStringField(tail, "aiTitle")
                ?? SessionLiteReader.ExtractLastJsonStringField(head, "aiTitle")
                ?? (firstPrompt.Length > 0 ? firstPrompt : null);
        }

        var (forkedSessionId, lines) = BuildForkLines(transcript, contentReplacements, sessionId, upToMessageId, title, DeriveTitle);

        var forkPath = Path.Combine(projectDir, $"{forkedSessionId}.jsonl");
        var payload = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(forkPath, options))
        {
            stream.Write(payload, 0, payload.Length);
        }

        return new ForkSessionResult(forkedSessionId);
    }

    // Core fork transform — remap UUIDs and produce serialized JSONL lines. Returns
    // (forkedSessionId, lines) where each line is a compact JSON string without a trailing newline
    // (matches PY).
    internal static (string ForkedSessionId, List<string> Lines) BuildForkLines(
        List<JsonObject> transcriptIn,
        List<JsonNode?> contentReplacements,
        string sessionId,
        string? upToMessageId,
        string? title,
        Func<string?> deriveTitle)
    {
        // Filter out sidechains (subagent sessions with separate parentUuid graphs). Keep isMeta
        // entries — they're interleaved in the main chain.
        var transcript = transcriptIn.Where(e => JsonHelpers.GetBool(e, "isSidechain") != true).ToList();

        if (transcript.Count == 0)
        {
            throw new InvalidOperationException($"Session {sessionId} has no messages to fork");
        }

        if (!string.IsNullOrEmpty(upToMessageId))
        {
            var cutoff = -1;
            for (var i = 0; i < transcript.Count; i++)
            {
                if (JsonHelpers.GetString(transcript[i], "uuid") == upToMessageId)
                {
                    cutoff = i;
                    break;
                }
            }

            if (cutoff == -1)
            {
                throw new InvalidOperationException($"Message {upToMessageId} not found in session {sessionId}");
            }

            transcript = transcript[..(cutoff + 1)];
        }

        // Include progress entries in the mapping — needed for the parentUuid chain walk.
        var uuidMapping = new Dictionary<string, string>();
        foreach (var entry in transcript)
        {
            uuidMapping[TranscriptSupport.GetUuid(entry)] = Guid.NewGuid().ToString();
        }

        // Filter out progress messages from written output. They're UI-only chain links; not needed
        // in a fresh fork.
        var writable = transcript.Where(e => JsonHelpers.GetString(e, "type") != "progress").ToList();
        if (writable.Count == 0)
        {
            throw new InvalidOperationException($"Session {sessionId} has no messages to fork");
        }

        var byUuid = new Dictionary<string, JsonObject>();
        foreach (var entry in transcript)
        {
            byUuid[TranscriptSupport.GetUuid(entry)] = entry;
        }

        var forkedSessionId = Guid.NewGuid().ToString();
        var now = FormatIsoNow();
        var lines = new List<string>();

        for (var i = 0; i < writable.Count; i++)
        {
            var original = writable[i];
            var newUuid = uuidMapping[TranscriptSupport.GetUuid(original)];

            // Resolve parentUuid, skipping progress ancestors.
            string? newParentUuid = null;
            var parentId = JsonHelpers.GetString(original, "parentUuid");
            while (!string.IsNullOrEmpty(parentId))
            {
                if (!byUuid.TryGetValue(parentId, out var parent))
                {
                    break;
                }

                if (JsonHelpers.GetString(parent, "type") != "progress")
                {
                    newParentUuid = uuidMapping.GetValueOrDefault(parentId);
                    break;
                }

                parentId = JsonHelpers.GetString(parent, "parentUuid");
            }

            // Only update the timestamp on the last message (leaf detection on resume).
            var timestamp = i == writable.Count - 1 ? now : (JsonHelpers.GetString(original, "timestamp") ?? now);

            // Remap logicalParentUuid (compact-boundary backpointer).
            var logicalParent = JsonHelpers.GetString(original, "logicalParentUuid");
            var newLogicalParent = !string.IsNullOrEmpty(logicalParent) ? uuidMapping.GetValueOrDefault(logicalParent) : logicalParent;

            var forked = (JsonObject)JsonNode.Parse(original.ToJsonString())!;
            forked["uuid"] = newUuid;
            forked["parentUuid"] = newParentUuid;
            forked["logicalParentUuid"] = newLogicalParent;
            forked["sessionId"] = forkedSessionId;
            forked["timestamp"] = timestamp;
            forked["isSidechain"] = false;
            forked["forkedFrom"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["messageUuid"] = TranscriptSupport.GetUuid(original),
            };

            // Remove fields that would leak state from the source session.
            foreach (var key in _strippedForkKeys)
            {
                forked.Remove(key);
            }

            lines.Add(forked.ToJsonString());
        }

        // Append content-replacement entry (if any) with the fork's sessionId.
        if (contentReplacements.Count > 0)
        {
            var replacementEntry = new JsonObject
            {
                ["type"] = "content-replacement",
                ["sessionId"] = forkedSessionId,
                ["replacements"] = new JsonArray([.. contentReplacements.Select(r => r?.DeepClone())]),
                ["uuid"] = Guid.NewGuid().ToString(),
                ["timestamp"] = now,
            };
            lines.Add(replacementEntry.ToJsonString());
        }

        // Derive title: explicit > original customTitle > original aiTitle > first prompt. Suffix
        // with " (fork)" for derived titles. ListSessions reads the LAST custom-title from the tail,
        // so this entry is what surfaces.
        var forkTitle = !string.IsNullOrEmpty(title) ? title.Trim() : null;
        if (string.IsNullOrEmpty(forkTitle))
        {
            forkTitle = $"{deriveTitle() ?? "Forked session"} (fork)";
        }

        var titleEntry = new JsonObject
        {
            ["type"] = "custom-title",
            ["sessionId"] = forkedSessionId,
            ["customTitle"] = forkTitle,
            ["uuid"] = Guid.NewGuid().ToString(),
            ["timestamp"] = now,
        };
        lines.Add(titleEntry.ToJsonString());

        return (forkedSessionId, lines);
    }

    // datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"). Always emits 6 fractional
    // digits, unlike Python's isoformat() which omits the fractional part entirely when microseconds
    // are exactly zero — an inconsequential difference since this value is only ever a pass-through
    // "timestamp" field, never re-parsed by this SDK. See PORTING_STATUS.md "Afwijkingen van PY".
    internal static string FormatIsoNow() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

    internal static readonly HashSet<string> _transcriptTypes = ["user", "assistant", "attachment", "system", "progress"];

    // Parses JSONL content into transcript entries + content-replacement records (matches PY).
    private static (List<JsonObject> Transcript, List<JsonNode?> ContentReplacements) ParseForkTranscript(byte[] content, string sessionId)
    {
        var transcript = new List<JsonObject>();
        var contentReplacements = new List<JsonNode?>();

        var text = Encoding.UTF8.GetString(content);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is not JsonObject entry)
            {
                continue;
            }

            var entryType = JsonHelpers.GetString(entry, "type");
            if (entryType is not null && _transcriptTypes.Contains(entryType) && JsonHelpers.GetString(entry, "uuid") is not null)
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

        return (transcript, contentReplacements);
    }

    // ---------------------------------------------------------------------
    // File lookup helpers
    // ---------------------------------------------------------------------

    private static string? FindSessionFile(string sessionId, string? directory) => FindSessionFileWithDir(sessionId, directory)?.FilePath;

    private static (string FilePath, string ProjectDir)? FindSessionFileWithDir(string sessionId, string? directory)
    {
        var fileName = $"{sessionId}.jsonl";

        static (string, string)? TryDir(string projectDir, string fileName)
        {
            var path = Path.Combine(projectDir, fileName);
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0)
                {
                    return (path, projectDir);
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
            var canonical = SessionPaths.CanonicalizePath(directory);
            var projectDir = SessionPaths.FindProjectDir(canonical);
            if (projectDir is not null)
            {
                var result = TryDir(projectDir, fileName);
                if (result is not null)
                {
                    return result;
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
                    var result = TryDir(wtProjectDir, fileName);
                    if (result is not null)
                    {
                        return result;
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
            var result = TryDir(entry, fileName);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static void AppendToSession(string sessionId, string data, string? directory)
    {
        var fileName = $"{sessionId}.jsonl";

        if (!string.IsNullOrEmpty(directory))
        {
            var canonical = SessionPaths.CanonicalizePath(directory);

            var projectDir = SessionPaths.FindProjectDir(canonical);
            if (projectDir is not null && SessionAppender.TryAppend(Path.Combine(projectDir, fileName), data))
            {
                return;
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
                if (wtProjectDir is not null && SessionAppender.TryAppend(Path.Combine(wtProjectDir, fileName), data))
                {
                    return;
                }
            }

            throw new FileNotFoundException($"Session {sessionId} not found in project directory for {directory}");
        }

        var projectsDir = SessionPaths.GetProjectsDir();
        List<string> dirents;
        try
        {
            dirents = [.. Directory.EnumerateFileSystemEntries(projectsDir)];
        }
        catch (Exception e)
        {
            throw new FileNotFoundException($"Session {sessionId} not found (no projects directory)", e);
        }

        foreach (var entry in dirents)
        {
            if (SessionAppender.TryAppend(Path.Combine(entry, fileName), data))
            {
                return;
            }
        }

        throw new FileNotFoundException($"Session {sessionId} not found in any project directory");
    }

    // ---------------------------------------------------------------------
    // Unicode sanitization (matches PY)
    // ---------------------------------------------------------------------

    internal static string SanitizeUnicode(string value)
    {
        var current = value;
        for (var i = 0; i < 10; i++)
        {
            var previous = current;
            current = current.Normalize(NormalizationForm.FormKC);
            current = StripFormatPrivateUnassigned(current);
            current = StripExplicitRanges(current);
            if (current == previous)
            {
                break;
            }
        }

        return current;
    }

    private static string StripFormatPrivateUnassigned(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(rune.Value);
            if (category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }

    private static string StripExplicitRanges(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            var v = rune.Value;
            var strip = (v >= 0x200b && v <= 0x200f)
                || (v >= 0x202a && v <= 0x202e)
                || (v >= 0x2066 && v <= 0x2069)
                || v == 0xfeff
                || (v >= 0xe000 && v <= 0xf8ff);
            if (!strip)
            {
                sb.Append(rune.ToString());
            }
        }

        return sb.ToString();
    }
}
