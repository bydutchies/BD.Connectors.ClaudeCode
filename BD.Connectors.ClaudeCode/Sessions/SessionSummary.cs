using System.Globalization;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Sessions.Internal;

namespace BD.Connectors.ClaudeCode.Sessions;

// FoldSessionSummary -- incremental session-summary derivation for ISessionStore adapters. Ported
// from PY. Fold lets a store maintain a per-session SessionSummaryEntry
// sidecar incrementally inside AppendAsync so ListSessionsFromStoreAsync can fetch all metadata in a
// single ListSessionSummariesAsync call instead of N per-session LoadAsync calls. Every derived field
// is append-incremental (set-once or last-wins) so adapters never need to re-read previously appended
// entries. Only Fold is part of PY's `__all__`; SummaryEntryToSdkInfo is an internal helper (PY:
// `summary_entry_to_sdk_info`, not re-exported from the package root either).
public static class SessionSummary
{
    // JSONL entry key -> SessionSummaryEntry Data key, for last-wins string fields (matches PY). Each
    // appended entry overwrites the previous value when present.
    private static readonly (string Src, string Dst)[] _lastWinsFields =
    [
        ("customTitle", "custom_title"),
        ("aiTitle", "ai_title"),
        ("lastPrompt", "last_prompt"),
        ("summary", "summary_hint"),
        ("gitBranch", "git_branch"),
    ];

    // Fold a batch of appended entries into the running summary for `key`. Stores call this from
    // inside AppendAsync to keep a SessionSummaryEntry sidecar up to date without re-reading the
    // transcript. `prev` is the previous summary for the same key (or null for the first append). Do
    // not call this for keys with a Subpath -- subagent transcripts must not contribute to the main
    // session's summary.
    //
    // Mtime is NOT touched by the fold -- it is the sidecar's storage write time and must be stamped
    // by the adapter after persisting (matches PY). For a new session (prev is null) the fold returns
    // Mtime=0 as a placeholder.
    public static SessionSummaryEntry Fold(SessionSummaryEntry? prev, SessionKey key, IReadOnlyList<JsonObject> entries)
    {
        var sessionId = prev?.SessionId ?? key.SessionId;
        var mtime = prev?.Mtime ?? 0;
        var data = prev is not null ? (JsonObject)prev.Data.DeepClone() : [];

        foreach (var entry in entries)
        {
            var ms = IsoToEpochMs(JsonHelpers.GetString(entry, "timestamp"));

            if (!data.ContainsKey("is_sidechain"))
            {
                data["is_sidechain"] = JsonHelpers.GetBool(entry, "isSidechain") == true;
            }

            if (!data.ContainsKey("created_at") && ms is not null)
            {
                data["created_at"] = ms.Value;
            }

            if (!data.ContainsKey("cwd"))
            {
                var cwd = JsonHelpers.GetString(entry, "cwd");
                if (!string.IsNullOrEmpty(cwd))
                {
                    data["cwd"] = cwd;
                }
            }

            FoldFirstPrompt(data, entry);

            foreach (var (src, dst) in _lastWinsFields)
            {
                var val = JsonHelpers.GetString(entry, src);
                if (val is not null)
                {
                    data[dst] = val;
                }
            }

            if (JsonHelpers.GetString(entry, "type") == "tag")
            {
                var tagVal = JsonHelpers.GetString(entry, "tag");
                if (!string.IsNullOrEmpty(tagVal))
                {
                    data["tag"] = tagVal;
                }
                else
                {
                    // Empty string or absent tag clears the tag.
                    data.Remove("tag");
                }
            }
        }

        return new SessionSummaryEntry(sessionId, mtime, data);
    }

    // Replicates ExtractFirstPromptFromHead for a single parsed entry. Mutates `data` in place: sets
    // first_prompt + first_prompt_locked on a real match, or stashes command_fallback for
    // slash-command messages. Skips tool_result, isMeta, isCompactSummary, and auto-generated
    // patterns, matching PY.
    private static void FoldFirstPrompt(JsonObject data, JsonObject entry)
    {
        if (JsonHelpers.GetBool(data, "first_prompt_locked") == true)
        {
            return;
        }

        if (JsonHelpers.GetString(entry, "type") != "user")
        {
            return;
        }

        if (JsonHelpers.GetBool(entry, "isMeta") == true || JsonHelpers.GetBool(entry, "isCompactSummary") == true)
        {
            return;
        }

        var message = JsonHelpers.GetObject(entry, "message");
        if (message is not null && JsonHelpers.GetNode(message, "content") is JsonArray contentBlocks
            && contentBlocks.Any(b => b is JsonObject bo && JsonHelpers.GetString(bo, "type") == "tool_result"))
        {
            return;
        }

        foreach (var raw in EntryTextBlocks(entry))
        {
            var result = raw.Replace('\n', ' ').Trim();
            if (result.Length == 0)
            {
                continue;
            }

            var cmdMatch = SessionLiteReader.MatchCommandName(result);
            if (cmdMatch.Success)
            {
                if (!data.ContainsKey("command_fallback"))
                {
                    data["command_fallback"] = cmdMatch.Groups[1].Value;
                }

                continue;
            }

            if (SessionLiteReader.IsSkippableFirstPrompt(result))
            {
                continue;
            }

            if (result.Length > 200)
            {
                result = result[..200].TrimEnd() + "…";
            }

            data["first_prompt"] = result;
            data["first_prompt_locked"] = true;
            return;
        }
    }

    // Extract text strings from a type=="user" entry's message content (matches PY).
    private static List<string> EntryTextBlocks(JsonObject entry)
    {
        var texts = new List<string>();
        var message = JsonHelpers.GetObject(entry, "message");
        if (message is null)
        {
            return texts;
        }

        var content = JsonHelpers.GetNode(message, "content");
        if (content is JsonValue value && value.TryGetValue<string>(out var s))
        {
            texts.Add(s);
        }
        else if (content is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block is JsonObject bo && JsonHelpers.GetString(bo, "type") == "text" && JsonHelpers.GetString(bo, "text") is { } text)
                {
                    texts.Add(text);
                }
            }
        }

        return texts;
    }

    private static long? IsoToEpochMs(string? ts)
    {
        if (string.IsNullOrEmpty(ts))
        {
            return null;
        }

        var norm = ts.EndsWith('Z') ? ts.Replace("Z", "+00:00") : ts;
        return DateTimeOffset.TryParse(norm, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    // Convert a SessionSummaryEntry to SdkSessionInfo. Returns null for sidechain sessions or
    // sessions with no extractable summary, matching SessionLiteReader.ParseSessionInfoFromLite's
    // filtering (matches PY). Internal: PY's `summary_entry_to_sdk_info` is not re-exported from the
    // package root.
    internal static SdkSessionInfo? SummaryEntryToSdkInfo(SessionSummaryEntry entry, string? projectPath)
    {
        var data = entry.Data;
        if (JsonHelpers.GetBool(data, "is_sidechain") == true)
        {
            return null;
        }

        var firstPrompt = JsonHelpers.GetBool(data, "first_prompt_locked") == true
            ? NullIfEmpty(JsonHelpers.GetString(data, "first_prompt"))
            : NullIfEmpty(JsonHelpers.GetString(data, "command_fallback"));

        var customTitle = NullIfEmpty(JsonHelpers.GetString(data, "custom_title")) ?? NullIfEmpty(JsonHelpers.GetString(data, "ai_title"));
        var summary = customTitle
            ?? NullIfEmpty(JsonHelpers.GetString(data, "last_prompt"))
            ?? NullIfEmpty(JsonHelpers.GetString(data, "summary_hint"))
            ?? firstPrompt;

        if (string.IsNullOrEmpty(summary))
        {
            return null;
        }

        return new SdkSessionInfo(
            entry.SessionId,
            summary,
            entry.Mtime,
            null,
            customTitle,
            firstPrompt,
            NullIfEmpty(JsonHelpers.GetString(data, "git_branch")),
            NullIfEmpty(JsonHelpers.GetString(data, "cwd")) ?? projectPath,
            NullIfEmpty(JsonHelpers.GetString(data, "tag")),
            JsonHelpers.GetInt64(data, "created_at"));
    }

    private static string? NullIfEmpty(string? v) => string.IsNullOrEmpty(v) ? null : v;
}
