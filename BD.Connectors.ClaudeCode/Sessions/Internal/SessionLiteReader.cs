using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// Result of reading a session file's head, tail, mtime and size (PY's `_LiteSessionFile`).
internal sealed class LiteSessionFile
{
    public required long Mtime { get; init; }

    public required long Size { get; init; }

    public required string Head { get; init; }

    public required string Tail { get; init; }
}

// Field extraction from a session file's head/tail without a full JSONL parse, plus the "lite"
// stat+head/tail read itself. Ported from PY.
internal static partial class SessionLiteReader
{
    // Size of the head/tail buffer for lite metadata reads.
    public const int LiteReadBufSize = 65536;

    [GeneratedRegex("<command-name>(.*?)</command-name>")]
    private static partial Regex CommandNameRegex();

    [GeneratedRegex(
        "^(?:<local-command-stdout>|<session-start-hook>|<tick>|<goal>|"
        + @"\[Request interrupted by user[^\]]*\]|"
        + @"\s*<ide_opened_file>[\s\S]*</ide_opened_file>\s*$|"
        + @"\s*<ide_selection>[\s\S]*</ide_selection>\s*$)")]
    private static partial Regex SkipFirstPromptRegex();

    // Shared with SessionSummary.FoldFirstPrompt (PY's `_fold_first_prompt`), which reuses the same
    // command-name/skip-pattern regexes as ExtractFirstPromptFromHead so the store
    // (incremental fold) and disk (head-buffer scan) paths agree on what counts as a "first prompt".
    internal static Match MatchCommandName(string text) => CommandNameRegex().Match(text);

    internal static bool IsSkippableFirstPrompt(string text) => SkipFirstPromptRegex().IsMatch(text);

    public static string? UnescapeJsonString(string raw)
    {
        if (!raw.Contains('\\'))
        {
            return raw;
        }

        try
        {
            var node = JsonNode.Parse("\"" + raw + "\"");
            return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    public static string? ExtractJsonStringField(string text, string key)
    {
        string[] patterns = [$"\"{key}\":\"", $"\"{key}\": \""];
        foreach (var pattern in patterns)
        {
            var idx = text.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var valueStart = idx + pattern.Length;
            var i = valueStart;
            while (i < text.Length)
            {
                if (text[i] == '\\')
                {
                    i += 2;
                    continue;
                }

                if (text[i] == '"')
                {
                    return UnescapeJsonString(text[valueStart..i]);
                }

                i++;
            }
        }

        return null;
    }

    public static string? ExtractLastJsonStringField(string text, string key)
    {
        string[] patterns = [$"\"{key}\":\"", $"\"{key}\": \""];
        string? lastValue = null;
        foreach (var pattern in patterns)
        {
            var searchFrom = 0;
            while (searchFrom <= text.Length)
            {
                var idx = text.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
                if (idx < 0)
                {
                    break;
                }

                var valueStart = idx + pattern.Length;
                var i = valueStart;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        lastValue = UnescapeJsonString(text[valueStart..i]);
                        break;
                    }

                    i++;
                }

                searchFrom = i + 1;
            }
        }

        return lastValue;
    }

    // Extracts the first meaningful user prompt from a JSONL head chunk (matches PY).
    public static string ExtractFirstPromptFromHead(string head)
    {
        var start = 0;
        var commandFallback = string.Empty;
        var headLen = head.Length;

        while (start < headLen)
        {
            string line;
            var newlineIdx = head.IndexOf('\n', start);
            if (newlineIdx >= 0)
            {
                line = head[start..newlineIdx];
                start = newlineIdx + 1;
            }
            else
            {
                line = head[start..];
                start = headLen;
            }

            if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && !line.Contains("\"type\": \"user\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("\"tool_result\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("\"isMeta\":true", StringComparison.Ordinal) || line.Contains("\"isMeta\": true", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("\"isCompactSummary\":true", StringComparison.Ordinal) || line.Contains("\"isCompactSummary\": true", StringComparison.Ordinal))
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

            if (node is not JsonObject entry || JsonHelpers.GetString(entry, "type") != "user")
            {
                continue;
            }

            if (JsonHelpers.GetObject(entry, "message") is not { } message)
            {
                continue;
            }

            var texts = new List<string>();
            var contentNode = JsonHelpers.GetNode(message, "content");
            if (contentNode is JsonValue contentValue && contentValue.TryGetValue<string>(out var contentStr))
            {
                texts.Add(contentStr);
            }
            else if (contentNode is JsonArray blocks)
            {
                foreach (var block in blocks)
                {
                    if (block is JsonObject blockObj
                        && JsonHelpers.GetString(blockObj, "type") == "text"
                        && JsonHelpers.GetString(blockObj, "text") is { } blockText)
                    {
                        texts.Add(blockText);
                    }
                }
            }

            foreach (var raw in texts)
            {
                var result = raw.Replace('\n', ' ').Trim();
                if (result.Length == 0)
                {
                    continue;
                }

                var cmdMatch = CommandNameRegex().Match(result);
                if (cmdMatch.Success)
                {
                    if (commandFallback.Length == 0)
                    {
                        commandFallback = cmdMatch.Groups[1].Value;
                    }

                    continue;
                }

                if (SkipFirstPromptRegex().IsMatch(result))
                {
                    continue;
                }

                if (result.Length > 200)
                {
                    result = result[..200].TrimEnd() + "…";
                }

                return result;
            }
        }

        return commandFallback.Length > 0 ? commandFallback : string.Empty;
    }

    // Opens a session file, stats it, and reads head + tail. Returns null on any error or empty file.
    public static LiteSessionFile? ReadSessionLite(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return null;
            }

            var size = info.Length;
            var mtime = (long)(info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var headBuf = new byte[LiteReadBufSize];
            var headRead = ReadFully(stream, headBuf);
            if (headRead == 0)
            {
                return null;
            }

            var head = Encoding.UTF8.GetString(headBuf, 0, headRead);

            string tail;
            var tailOffset = Math.Max(0, size - LiteReadBufSize);
            if (tailOffset == 0)
            {
                tail = head;
            }
            else
            {
                stream.Seek(tailOffset, SeekOrigin.Begin);
                var tailBuf = new byte[LiteReadBufSize];
                var tailRead = ReadFully(stream, tailBuf);
                tail = Encoding.UTF8.GetString(tailBuf, 0, tailRead);
            }

            return new LiteSessionFile { Mtime = mtime, Size = size, Head = head, Tail = tail };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Parses SdkSessionInfo fields from a lite session read (head/tail/stat). Returns null for
    // sidechain sessions or metadata-only sessions with no extractable summary. Shared by
    // ListSessionsAsync and GetSessionInfoAsync (matches PY).
    public static SdkSessionInfo? ParseSessionInfoFromLite(string sessionId, LiteSessionFile lite, string? projectPath = null)
    {
        var head = lite.Head;
        var tail = lite.Tail;

        var firstNewline = head.IndexOf('\n');
        var firstLine = firstNewline >= 0 ? head[..firstNewline] : head;
        if (firstLine.Contains("\"isSidechain\":true", StringComparison.Ordinal) || firstLine.Contains("\"isSidechain\": true", StringComparison.Ordinal))
        {
            return null;
        }

        var customTitle = FirstNonEmpty(
            ExtractLastJsonStringField(tail, "customTitle"),
            ExtractLastJsonStringField(head, "customTitle"),
            ExtractLastJsonStringField(tail, "aiTitle"),
            ExtractLastJsonStringField(head, "aiTitle"));

        var firstPromptRaw = ExtractFirstPromptFromHead(head);
        var firstPrompt = firstPromptRaw.Length > 0 ? firstPromptRaw : null;

        var summary = FirstNonEmpty(
            customTitle,
            ExtractLastJsonStringField(tail, "lastPrompt"),
            ExtractLastJsonStringField(tail, "summary"),
            firstPrompt);

        if (string.IsNullOrEmpty(summary))
        {
            return null;
        }

        var gitBranch = FirstNonEmpty(
            ExtractLastJsonStringField(tail, "gitBranch"),
            ExtractJsonStringField(head, "gitBranch"));

        var sessionCwd = FirstNonEmpty(ExtractJsonStringField(head, "cwd"), projectPath);

        // Scope tag extraction to {"type":"tag"} lines — a bare tail scan for "tag" would match
        // tool_use inputs (git tag, Docker tags, cloud resource tags).
        string? tagLine = null;
        var tailLines = tail.Split('\n');
        for (var i = tailLines.Length - 1; i >= 0; i--)
        {
            if (tailLines[i].StartsWith("{\"type\":\"tag\"", StringComparison.Ordinal))
            {
                tagLine = tailLines[i];
                break;
            }
        }

        var tag = tagLine is not null ? NullIfEmpty(ExtractLastJsonStringField(tagLine, "tag")) : null;

        long? createdAt = null;
        var firstTimestamp = ExtractJsonStringField(head, "timestamp");
        if (!string.IsNullOrEmpty(firstTimestamp))
        {
            var ts = firstTimestamp.EndsWith('Z') ? firstTimestamp.Replace("Z", "+00:00") : firstTimestamp;
            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                createdAt = parsed.ToUnixTimeMilliseconds();
            }
        }

        return new SdkSessionInfo(
            sessionId,
            summary!,
            lite.Mtime,
            lite.Size,
            customTitle,
            firstPrompt,
            gitBranch,
            sessionCwd,
            tag,
            createdAt);
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string? NullIfEmpty(string? v) => string.IsNullOrEmpty(v) ? null : v;
}
