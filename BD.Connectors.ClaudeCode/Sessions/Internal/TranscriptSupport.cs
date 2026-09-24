using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// Shared by SessionsCore and SessionMutationsCore: transcript entry types that carry uuid +
// parentUuid chain links, matching PY.
internal static class TranscriptSupport
{
    public static readonly HashSet<string> TranscriptEntryTypes = ["user", "assistant", "progress", "system", "attachment"];

    public static string GetUuid(JsonObject entry) => JsonHelpers.GetString(entry, "uuid") ?? string.Empty;

    // Separates the synthetic "agent_metadata" entry from transcript lines in a SessionStore-loaded
    // subagent stream (PY's `_split_agent_metadata`). Returns (metadata, transcript) where metadata is
    // the LAST such entry (it is rewritten on resume, so last wins) or null. Shared by SessionResume
    // and the store-backed subagent reads.
    public static (JsonObject? Metadata, List<JsonObject> Transcript) SplitAgentMetadata(IReadOnlyList<JsonObject> entries)
    {
        JsonObject? metadata = null;
        var transcript = new List<JsonObject>();
        foreach (var entry in entries)
        {
            if (JsonHelpers.GetString(entry, "type") == "agent_metadata")
            {
                metadata = entry;
            }
            else
            {
                transcript.Add(entry);
            }
        }

        return (metadata, transcript);
    }

    // Extracts (toolUseId, parentAgentId) from an agent metadata object -- works for both the on-disk
    // .meta.json sidecar and the synthetic agent_metadata entry a SessionStore receives in its place
    // (PY's `_parent_ids_from_agent_metadata`).
    public static (string? ToolUseId, string? ParentAgentId) ParentIdsFromAgentMetadata(JsonObject? meta) =>
        meta is null ? (null, null) : (JsonHelpers.GetString(meta, "toolUseId"), JsonHelpers.GetString(meta, "parentAgentId"));

    // Parses JSONL content into transcript entries. Only keeps entries that have a uuid and are
    // transcript message types; skips corrupt lines (matches PY).
    public static List<JsonObject> ParseTranscriptEntries(string content)
    {
        var entries = new List<JsonObject>();
        var start = 0;
        var length = content.Length;

        while (start < length)
        {
            var end = content.IndexOf('\n', start);
            if (end == -1)
            {
                end = length;
            }

            var line = content[start..end].Trim();
            start = end + 1;
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (node is not JsonObject entry)
            {
                continue;
            }

            var type = JsonHelpers.GetString(entry, "type");
            if (type is not null && TranscriptEntryTypes.Contains(type) && JsonHelpers.GetString(entry, "uuid") is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }
}
