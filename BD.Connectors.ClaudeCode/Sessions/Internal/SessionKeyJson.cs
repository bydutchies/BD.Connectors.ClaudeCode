using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// Converts a SessionKey to the wire shape used for the synthetic mirror_error system message's "key"
// field (bijlage A), matching PY's SessionKey TypedDict field names (project_key/session_id/subpath)
// exactly since PY passes the TypedDict straight through to json.dumps.
internal static class SessionKeyJson
{
    public static JsonObject ToJson(SessionKey key)
    {
        var obj = new JsonObject
        {
            ["project_key"] = key.ProjectKey,
            ["session_id"] = key.SessionId,
        };

        // PY's SessionKey.subpath is NotRequired[str] -- omitted (not null) for the main transcript.
        if (!string.IsNullOrEmpty(key.Subpath))
        {
            obj["subpath"] = key.Subpath;
        }

        return obj;
    }
}
