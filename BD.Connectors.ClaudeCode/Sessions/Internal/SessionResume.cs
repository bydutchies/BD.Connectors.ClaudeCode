using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Logging;
using BD.Connectors.ClaudeCode.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// Materialize a SessionStore-backed resume into a temp CLAUDE_CONFIG_DIR. Ported from PY.
//
// When options.Resume (or options.ContinueConversation) is paired with options.SessionStore, the
// session JSONL almost certainly does not exist on local disk -- it lives in the external store. The
// CLI subprocess only knows how to resume from a local file. This module bridges the gap: it loads
// the session from the store, writes it to a temporary directory laid out exactly like ~/.claude/,
// and returns the path so the caller can point the subprocess at it via CLAUDE_CONFIG_DIR.
internal static class SessionResume
{
    private const string KeychainServiceName = "Claude Code-credentials";
    private static readonly string[] _resumeSettingsStrippedKeys = ["enabledPlugins", "extraKnownMarketplaces"];

    internal sealed record MaterializedResume(string ConfigDir, string ResumeSessionId, Func<Task> Cleanup);

    // Returns a copy of `options` repointed at a materialized temp config dir: CLAUDE_CONFIG_DIR in
    // Env, Resume set to the materialized session id, ContinueConversation cleared (already resolved
    // to a concrete session id during materialization).
    public static ClaudeAgentOptions ApplyMaterializedOptions(ClaudeAgentOptions options, MaterializedResume materialized)
    {
        var env = new Dictionary<string, string>(options.Env) { [WireConstants.EnvVars.ClaudeConfigDir] = materialized.ConfigDir };
        return options with { Env = env, Resume = materialized.ResumeSessionId, ContinueConversation = false };
    }

    // Constructs the TranscriptMirrorBatcher for a session. Resolves projectsDir to the materialized
    // temp dir when present (so file_path -> key resolution matches what the subprocess writes),
    // otherwise to the standard projects directory under the effective CLAUDE_CONFIG_DIR.
    // flushMode=Eager zeroes the batcher's pending thresholds so every enqueued frame schedules a
    // background flush; Batched keeps the defaults.
    public static TranscriptMirrorBatcher BuildMirrorBatcher(
        ISessionStore store,
        MaterializedResume? materialized,
        IReadOnlyDictionary<string, string>? env,
        Func<SessionKey?, string, Task> onError,
        SessionStoreFlushMode flushMode,
        ILogger? logger)
    {
        var projectsDir = materialized is not null
            ? Path.Combine(materialized.ConfigDir, "projects")
            : SessionPaths.GetProjectsDir(env);
        var eager = flushMode == SessionStoreFlushMode.Eager;
        return new TranscriptMirrorBatcher(
            store,
            projectsDir,
            onError,
            maxPendingEntries: eager ? 0 : TranscriptMirrorBatcher.DefaultMaxPendingEntries,
            maxPendingBytes: eager ? 0 : TranscriptMirrorBatcher.DefaultMaxPendingBytes,
            logger: logger);
    }

    // Loads a session from options.SessionStore and writes it to a temp dir. Returns null when no
    // materialization is needed (no store, no resume/continue, store has no entries, or the resolved
    // session ID is not a valid UUID) -- caller falls through to the normal (no-store) resume/spawn
    // path. Throws InvalidOperationException (PY: RuntimeError) if a store call fails or times out.
    public static async Task<MaterializedResume?> MaterializeResumeSessionAsync(
        ClaudeAgentOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var store = options.SessionStore;
        if (store is null)
        {
            return null;
        }

        if (options.Resume is null && !options.ContinueConversation)
        {
            return null;
        }

        var timeout = TimeSpan.FromMilliseconds(options.LoadTimeoutMs);
        var projectKey = ClaudeSessions.ProjectKeyForDirectory(options.Cwd);

        (string SessionId, IReadOnlyList<JsonObject> Entries)? resolved;
        if (options.Resume is not null)
        {
            // session_id is used as a path component below; reject anything that isn't a UUID to
            // prevent traversal and match every other resume path.
            if (SessionPaths.ValidateUuid(options.Resume) is null)
            {
                return null;
            }

            resolved = await LoadCandidateAsync(store, projectKey, options.Resume, timeout, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            resolved = await ResolveContinueCandidateAsync(store, projectKey, timeout, cancellationToken).ConfigureAwait(false);
        }

        if (resolved is null)
        {
            return null;
        }

        var (sessionId, entries) = resolved.Value;

        var tmpBase = Directory.CreateTempSubdirectory("claude-resume-").FullName;
        try
        {
            var projectDir = Path.Combine(tmpBase, "projects", projectKey);
            Directory.CreateDirectory(projectDir);
            WriteJsonl(Path.Combine(projectDir, $"{sessionId}.jsonl"), entries);

            // The subprocess will run with CLAUDE_CONFIG_DIR=tmpBase. Copy auth config from the
            // caller's effective config locations so it can authenticate. Missing files are fine
            // (API-key auth, etc.).
            CopyAuthFiles(tmpBase, options.Env, logger);

            // Materialize subagent transcripts if the store can enumerate them.
            if (store is ISessionStoreSubkeys subkeys)
            {
                await MaterializeSubkeysAsync(store, subkeys, projectDir, projectKey, sessionId, timeout, logger, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Any failure after CreateTempSubdirectory leaves tmpBase (which may already contain a
            // .credentials.json copy) on disk with no path for the caller to clean it up. Remove it
            // before rethrowing.
            await RmtreeWithRetryAsync(tmpBase).ConfigureAwait(false);
            throw;
        }

        return new MaterializedResume(tmpBase, sessionId, () => RmtreeWithRetryAsync(tmpBase));
    }

    // ---------------------------------------------------------------------
    // Candidate resolution
    // ---------------------------------------------------------------------

    private static async Task<(string SessionId, IReadOnlyList<JsonObject> Entries)?> LoadCandidateAsync(
        ISessionStore store, string projectKey, string sessionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var entries = await WithTimeoutAsync(
            store.LoadAsync(new SessionKey(projectKey, sessionId), cancellationToken),
            timeout,
            $"SessionStore.load() for session {sessionId}").ConfigureAwait(false);
        return entries is null || entries.Count == 0 ? null : (sessionId, entries);
    }

    // Picks the most-recently-modified non-sidechain session. Sidechain transcripts are mirrored as
    // ordinary top-level keys and often have the highest mtime (their append lands after the main
    // session's in the same flush). Walk newest to oldest, loading each candidate (the load is needed
    // anyway) and skipping sidechains so continue_conversation resumes the user's conversation, not a
    // subagent's.
    private static async Task<(string SessionId, IReadOnlyList<JsonObject> Entries)?> ResolveContinueCandidateAsync(
        ISessionStore store, string projectKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (store is not ISessionStoreListing listing)
        {
            // SessionStoreValidation requires list_sessions() for continue+session_store without an
            // explicit resume, so this should not normally be reached.
            return null;
        }

        var sessions = await WithTimeoutAsync(listing.ListSessionsAsync(projectKey, cancellationToken), timeout, "SessionStore.list_sessions()").ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            return null;
        }

        foreach (var candidate in sessions.OrderByDescending(s => s.Mtime))
        {
            if (SessionPaths.ValidateUuid(candidate.SessionId) is null)
            {
                continue;
            }

            var loaded = await LoadCandidateAsync(store, projectKey, candidate.SessionId, timeout, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                continue;
            }

            if (JsonHelpers.GetBool(loaded.Value.Entries[0], "isSidechain") == true)
            {
                continue;
            }

            return loaded;
        }

        return null;
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string what)
    {
        try
        {
            return await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new InvalidOperationException($"{what} timed out after {(int)timeout.TotalMilliseconds}ms during resume materialization", e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException($"{what} failed during resume materialization: {e.Message}", e);
        }
    }

    // ---------------------------------------------------------------------
    // Filesystem helpers
    // ---------------------------------------------------------------------

    private static void WriteJsonl(string path, IReadOnlyList<JsonObject> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (var entry in entries)
            {
                writer.Write(entry.ToJsonString());
                writer.Write('\n');
            }
        }

        TryChmod600(path);
    }

    private static void TryChmod600(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Seeds tmpBase with the caller's auth and user config: .credentials.json (refreshToken redacted),
    // .claude.json, and user settings.json / cowork_settings.json (plugin declarations stripped).
    private static void CopyAuthFiles(string tmpBase, IReadOnlyDictionary<string, string> optEnv, ILogger? logger)
    {
        var callerConfigDir = GetEnvValue(optEnv, WireConstants.EnvVars.ClaudeConfigDir) ?? Environment.GetEnvironmentVariable(WireConstants.EnvVars.ClaudeConfigDir);
        var sourceConfigDir = !string.IsNullOrEmpty(callerConfigDir)
            ? callerConfigDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        var credsBytes = ReadIfPresent(Path.Combine(sourceConfigDir, ".credentials.json"), logger);
        var credsJson = credsBytes is not null ? Encoding.UTF8.GetString(credsBytes) : null;

        // macOS default setup keeps OAuth tokens in the Keychain, not a file. Redirecting
        // CLAUDE_CONFIG_DIR changes the Keychain service-name suffix, so the subprocess's lookup
        // misses and falls back to plainTextStorage at tmpBase/.credentials.json. Populate that file
        // from the parent's Keychain so the resumed subprocess can auth. Skipped when env-based auth
        // or a custom config dir is already in play.
        if (string.IsNullOrEmpty(callerConfigDir)
            && string.IsNullOrEmpty(GetEnvValue(optEnv, "ANTHROPIC_API_KEY") ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            && string.IsNullOrEmpty(GetEnvValue(optEnv, "CLAUDE_CODE_OAUTH_TOKEN") ?? Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN")))
        {
            var keychain = ReadKeychainCredentials();
            if (keychain is not null)
            {
                credsJson = keychain;
            }
        }

        WriteRedactedCredentials(credsJson, Path.Combine(tmpBase, ".credentials.json"));

        var claudeJsonSrc = !string.IsNullOrEmpty(callerConfigDir)
            ? Path.Combine(callerConfigDir, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        CopyIfPresent(claudeJsonSrc, Path.Combine(tmpBase, ".claude.json"), null, logger);

        // User settings carry apiKeyHelper (a fourth auth mechanism alongside .credentials.json /
        // Keychain / env) plus env/hooks/permissions. cowork_settings.json is the alternate filename
        // the CLI reads in cowork-plugins mode. Both pass through StripSettingsForResume so plugin
        // declarations don't reconcile against the empty tmpBase plugin cache.
        foreach (var name in new[] { "settings.json", "cowork_settings.json" })
        {
            CopyIfPresent(Path.Combine(sourceConfigDir, name), Path.Combine(tmpBase, name), StripSettingsForResume, logger);
        }
    }

    private static string? GetEnvValue(IReadOnlyDictionary<string, string>? env, string key) =>
        env is not null && env.TryGetValue(key, out var value) ? value : null;

    private static byte[]? ReadIfPresent(string src, ILogger? logger)
    {
        if (!File.Exists(src))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(src);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SdkLog.ResumeSkippingFile(logger ?? NullLogger.Instance, src, e);
            return null;
        }
    }

    private static void CopyIfPresent(string src, string dst, Func<byte[], byte[]>? transform, ILogger? logger)
    {
        var content = ReadIfPresent(src, logger);
        if (content is null)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dst, transform is not null ? transform(content) : content);
            TryChmod600(dst);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Don't leave a truncated dst behind for the subprocess to misparse.
            try
            {
                File.Delete(dst);
            }
            catch (Exception)
            {
                // Best-effort cleanup.
            }

            SdkLog.ResumeSkippingFile(logger ?? NullLogger.Instance, src, e);
        }
    }

    // Drops settings keys that misbehave under a redirected config dir: plugin declarations reconcile
    // against the always-empty tmpBase/plugins cache and would network-install each declared
    // marketplace on every resume; env.CLAUDE_CONFIG_DIR would point the subprocess's config reads
    // away from tmpBase. Content that doesn't parse as a JSON object is returned untouched.
    private static byte[] StripSettingsForResume(byte[] content)
    {
        JsonNode? parsed;
        try
        {
            // A leading UTF-8 BOM mirrors the CLI's settings reader (PowerShell writes settings.json
            // with one).
            parsed = JsonNode.Parse(DecodeUtf8WithOptionalBom(content));
        }
        catch (JsonException)
        {
            return content;
        }

        if (parsed is not JsonObject obj)
        {
            return content;
        }

        var stripped = false;
        foreach (var key in _resumeSettingsStrippedKeys)
        {
            if (obj.Remove(key))
            {
                stripped = true;
            }
        }

        if (JsonHelpers.GetObject(obj, "env") is { } envBlock && envBlock.Remove(WireConstants.EnvVars.ClaudeConfigDir))
        {
            stripped = true;
        }

        return stripped ? Encoding.UTF8.GetBytes(obj.ToJsonString()) : content;
    }

    private static string DecodeUtf8WithOptionalBom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);
    }

    // Writes creds_json with claudeAiOauth.refreshToken removed. The resumed subprocess runs under a
    // redirected CLAUDE_CONFIG_DIR; if it refreshed, the single-use refresh token would be consumed
    // server-side and the new tokens written to a location the parent never reads back -- leaving the
    // parent's stored creds revoked. With no refreshToken, the subprocess's refresh check
    // short-circuits.
    private static void WriteRedactedCredentials(string? credsJson, string dst)
    {
        if (credsJson is null)
        {
            return;
        }

        var output = credsJson;
        try
        {
            if (JsonNode.Parse(credsJson) is JsonObject data
                && JsonHelpers.GetObject(data, "claudeAiOauth") is { } oauth
                && oauth.ContainsKey("refreshToken"))
            {
                oauth.Remove("refreshToken");
                output = data.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Unparseable -- write through; subprocess will fail to parse it too.
        }

        File.WriteAllText(dst, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TryChmod600(dst);
    }

    // Reads OAuth credentials JSON from the macOS Keychain (default service name). Best-effort --
    // returns null on any error or non-macOS platforms.
    private static string? ReadKeychainCredentials()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        string user;
        try
        {
            user = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
        }
        catch (Exception)
        {
            user = "claude-code-user";
        }

        try
        {
            var startInfo = new ProcessStartInfo("security")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("find-generic-password");
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add(user);
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(KeychainServiceName);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Best-effort cleanup for a timed-out probe.
                }

                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && stdout.Length > 0 ? stdout : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Subkey (subagent) materialization
    // ---------------------------------------------------------------------

    private static async Task MaterializeSubkeysAsync(
        ISessionStore store,
        ISessionStoreSubkeys subkeys,
        string projectDir,
        string projectKey,
        string sessionId,
        TimeSpan timeout,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var sessionDir = Path.Combine(projectDir, sessionId);
        var subpaths = await WithTimeoutAsync(
            subkeys.ListSubkeysAsync(new SessionListSubkeysKey(projectKey, sessionId), cancellationToken),
            timeout,
            $"SessionStore.list_subkeys() for session {sessionId}").ConfigureAwait(false);

        foreach (var subpath in subpaths)
        {
            // Subpaths come from an external store and are used as filesystem path components below.
            // Reject anything that would escape the session directory.
            if (!IsSafeSubpath(subpath, sessionDir))
            {
                SdkLog.ResumeSkippingUnsafeSubpath(logger ?? NullLogger.Instance, subpath);
                continue;
            }

            var subKey = new SessionKey(projectKey, sessionId, subpath);
            var subEntries = await WithTimeoutAsync(
                store.LoadAsync(subKey, cancellationToken),
                timeout,
                $"SessionStore.load() for session {sessionId} subpath {subpath}").ConfigureAwait(false);
            if (subEntries is null || subEntries.Count == 0)
            {
                continue;
            }

            // agent_metadata entries describe the .meta.json sidecar (last one wins); everything else
            // is a transcript line.
            var (metadata, transcript) = TranscriptSupport.SplitAgentMetadata(subEntries);

            var subFile = Path.Combine(sessionDir, subpath.Replace('/', Path.DirectorySeparatorChar)) + ".jsonl";
            if (transcript.Count > 0)
            {
                WriteJsonl(subFile, transcript);
            }

            if (metadata is not null)
            {
                var metaContent = (JsonObject)metadata.DeepClone();
                metaContent.Remove("type");
                var metaFile = AgentMetadataSidecarPath(subFile);
                Directory.CreateDirectory(Path.GetDirectoryName(metaFile)!);
                File.WriteAllText(metaFile, metaContent.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                TryChmod600(metaFile);
            }
        }
    }

    // "agent-<id>.jsonl" -> "agent-<id>.meta.json" (same directory).
    private static string AgentMetadataSidecarPath(string transcriptPath) => transcriptPath[..^".jsonl".Length] + ".meta.json";

    // Rejects subpaths that are empty, absolute, contain "." / "..", embed a NUL, or resolve outside
    // sessionDir.
    private static bool IsSafeSubpath(string subpath, string sessionDir)
    {
        if (string.IsNullOrEmpty(subpath))
        {
            return false;
        }

        if (Path.IsPathRooted(subpath) || subpath.StartsWith('/') || subpath.StartsWith('\\'))
        {
            return false;
        }

        // Drive-prefixed ("C:foo") subpaths are never legitimate store keys, checked regardless of
        // host OS.
        if (subpath.Length >= 2 && subpath[1] == ':' && char.IsAsciiLetter(subpath[0]))
        {
            return false;
        }

        if (subpath.Split(['\\', '/']).Any(part => part is "." or ".."))
        {
            return false;
        }

        if (subpath.Contains('\0'))
        {
            return false;
        }

        try
        {
            var target = Path.Combine(sessionDir, subpath.Replace('/', Path.DirectorySeparatorChar));
            var subFile = Path.GetFullPath(target + ".jsonl");
            var resolvedSessionDir = Path.GetFullPath(sessionDir) + Path.DirectorySeparatorChar;
            return subFile.StartsWith(resolvedSessionDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Cleanup
    // ---------------------------------------------------------------------

    // Best-effort recursive delete with retries on transient lock errors (Windows AV/indexer can
    // briefly hold a handle on freshly-written files, notably .credentials.json). Never raises.
    private static async Task RmtreeWithRetryAsync(string path, int retries = 4)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var i = 0; i < retries; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Final best-effort sweep; give up silently so a cancelled/failed resume never leaks an
            // exception from cleanup.
        }
    }
}
