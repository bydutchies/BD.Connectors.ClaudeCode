namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// FilePathToSessionKey -- derives a SessionKey from an absolute transcript file path. Ported from
// PY's `file_path_to_session_key`. Not part of PY's top-level `__all__` (only re-exported from its
// own internal session-store module, used by TranscriptMirrorBatcher and its own tests), hence
// internal here too.
internal static class SessionStoreFileKeys
{
    // Main transcripts: <projectsDir>/<projectKey>/<sessionId>.jsonl
    // Subagent transcripts: <projectsDir>/<projectKey>/<sessionId>/subagents/agent-<id>.jsonl
    // Returns null if filePath is not under projectsDir or has an unrecognized shape.
    public static SessionKey? FilePathToSessionKey(string filePath, string projectsDir)
    {
        string rel;
        try
        {
            rel = Path.GetRelativePath(projectsDir, filePath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // .NET returns the unmodified (rooted) `filePath` when the two paths share no common root
        // (e.g. different drives on Windows) instead of throwing -- treat that the same way PY's
        // `except ValueError` does: "not under projects_dir".
        if (string.IsNullOrEmpty(rel) || rel == "." || Path.IsPathRooted(rel))
        {
            return null;
        }

        var parts = rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] == "..")
        {
            return null;
        }

        if (parts.Length < 2)
        {
            return null;
        }

        var projectKey = parts[0];
        var second = parts[1];

        // Main transcript: <projectKey>/<sessionId>.jsonl
        if (parts.Length == 2 && second.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return new SessionKey(projectKey, second[..^".jsonl".Length]);
        }

        // Subagent transcript: <projectKey>/<sessionId>/subagents/.../agent-<id>.jsonl
        if (parts.Length >= 4)
        {
            var subpathParts = parts[2..];
            var last = subpathParts[^1];
            if (last.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                subpathParts[^1] = last[..^".jsonl".Length];
            }

            // Subpaths are always /-joined regardless of OS separator so keys are portable across
            // platforms.
            return new SessionKey(projectKey, second, string.Join('/', subpathParts));
        }

        return null;
    }
}
