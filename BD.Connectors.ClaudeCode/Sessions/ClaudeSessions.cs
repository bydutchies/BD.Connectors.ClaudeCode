using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Sessions.Internal;

namespace BD.Connectors.ClaudeCode.Sessions;

// Public local-session API — reads and mutates ~/.claude/projects/<sanitized-cwd>/<sessionId>.jsonl
// directly, without going through the CLI. Ported from PY's local session listing and mutation
// functions. The PY functions are fully synchronous (blocking
// file I/O, and for worktree-aware listing, a `git` subprocess call); each method here wraps that
// same synchronous work in Task.Run so callers get a Task-based API without this SDK inventing async
// file I/O PY doesn't have.
public static class ClaudeSessions
{
    public static async Task<IReadOnlyList<SdkSessionInfo>> ListSessionsAsync(
        string? directory = null, int? limit = null, int offset = 0, bool includeWorktrees = true, CancellationToken ct = default)
    {
        return await Task.Run(() => SessionsCore.ListSessions(directory, limit, offset, includeWorktrees), ct).ConfigureAwait(false);
    }

    public static async Task<SdkSessionInfo?> GetSessionInfoAsync(string sessionId, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return await Task.Run(() => SessionsCore.GetSessionInfo(sessionId, directory), ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<SessionMessage>> GetSessionMessagesAsync(
        string sessionId, string? directory = null, int? limit = null, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return await Task.Run(() => SessionsCore.GetSessionMessages(sessionId, directory, limit, offset), ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<string>> ListSubagentsAsync(string sessionId, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return await Task.Run(() => SubagentSessions.ListSubagents(sessionId, directory), ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<SessionMessage>> GetSubagentMessagesAsync(
        string sessionId, string agentId, string? directory = null, int? limit = null, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(agentId);
        return await Task.Run(() => SubagentSessions.GetSubagentMessages(sessionId, agentId, directory, limit, offset), ct).ConfigureAwait(false);
    }

    public static async Task RenameSessionAsync(string sessionId, string title, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        await Task.Run(() => SessionMutationsCore.RenameSession(sessionId, title, directory), ct).ConfigureAwait(false);
    }

    public static async Task TagSessionAsync(string sessionId, string? tag, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await Task.Run(() => SessionMutationsCore.TagSession(sessionId, tag, directory), ct).ConfigureAwait(false);
    }

    public static async Task DeleteSessionAsync(string sessionId, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await Task.Run(() => SessionMutationsCore.DeleteSession(sessionId, directory), ct).ConfigureAwait(false);
    }

    public static async Task<ForkSessionResult> ForkSessionAsync(
        string sessionId, string? directory = null, string? upToMessageId = null, string? title = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return await Task.Run(() => SessionMutationsCore.ForkSession(sessionId, directory, upToMessageId, title), ct).ConfigureAwait(false);
    }

    // Derives the SessionStore project_key for a directory (defaults to the current working
    // directory). Uses the same realpath + NFC normalization + hashed sanitization the CLI uses for
    // project directory names, so keys match between local-disk transcripts and store-mirrored
    // transcripts. Synchronous (no I/O beyond a realpath resolution) — matches PY's
    // project_key_for_directory, which is sync in PY too.
    public static string ProjectKeyForDirectory(string? directory = null)
    {
        var absPath = SessionPaths.CanonicalizePath(directory ?? ".");
        return SessionPaths.SanitizePath(absPath);
    }

    // ------------------------------------------------------------------
    // SessionStore-backed reads (F7.6) — async counterparts to the local (disk) reads above.
    // ------------------------------------------------------------------

    public static Task<IReadOnlyList<SdkSessionInfo>> ListSessionsFromStoreAsync(
        ISessionStore sessionStore, string? directory = null, int? limit = null, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        return SessionStoreReads.ListSessionsFromStoreAsync(sessionStore, directory, limit, offset, logger: null, ct);
    }

    public static Task<SdkSessionInfo?> GetSessionInfoFromStoreAsync(
        ISessionStore sessionStore, string sessionId, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        return SessionStoreReads.GetSessionInfoFromStoreAsync(sessionStore, sessionId, directory, ct);
    }

    public static Task<IReadOnlyList<SessionMessage>> GetSessionMessagesFromStoreAsync(
        ISessionStore sessionStore, string sessionId, string? directory = null, int? limit = null, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        return SessionStoreReads.GetSessionMessagesFromStoreAsync(sessionStore, sessionId, directory, limit, offset, ct);
    }

    public static Task<IReadOnlyList<string>> ListSubagentsFromStoreAsync(
        ISessionStore sessionStore, string sessionId, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        return SessionStoreReads.ListSubagentsFromStoreAsync(sessionStore, sessionId, directory, ct);
    }

    public static Task<IReadOnlyList<SessionMessage>> GetSubagentMessagesFromStoreAsync(
        ISessionStore sessionStore, string sessionId, string agentId, string? directory = null, int? limit = null, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(agentId);
        return SessionStoreReads.GetSubagentMessagesFromStoreAsync(sessionStore, sessionId, agentId, directory, limit, offset, ct);
    }

    // ------------------------------------------------------------------
    // SessionStore-backed mutations (F7.7) — async counterparts to the local (disk) mutations above.
    // ------------------------------------------------------------------

    public static Task RenameSessionViaStoreAsync(ISessionStore sessionStore, string sessionId, string title, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        return SessionMutationsStore.RenameSessionViaStoreAsync(sessionStore, sessionId, title, directory, ct);
    }

    public static Task TagSessionViaStoreAsync(ISessionStore sessionStore, string sessionId, string? tag, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        return SessionMutationsStore.TagSessionViaStoreAsync(sessionStore, sessionId, tag, directory, ct);
    }

    public static Task DeleteSessionViaStoreAsync(ISessionStore sessionStore, string sessionId, string? directory = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        return SessionMutationsStore.DeleteSessionViaStoreAsync(sessionStore, sessionId, directory, ct);
    }

    public static Task<ForkSessionResult> ForkSessionViaStoreAsync(
        ISessionStore sessionStore, string sessionId, string? directory = null, string? upToMessageId = null, string? title = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(sessionId);
        return SessionMutationsStore.ForkSessionViaStoreAsync(sessionStore, sessionId, directory, upToMessageId, title, ct);
    }

    // Replays a local on-disk session transcript into an ISessionStore (F7.8) — the inverse of resume
    // materialization. Useful for migrating existing local sessions to a remote store, or for catching
    // a store up after a MirrorErrorMessage indicated a live-mirror gap.
    public static Task ImportSessionToStoreAsync(
        string sessionId,
        ISessionStore sessionStore,
        string? directory = null,
        bool includeSubagents = true,
        int batchSize = SessionImport.DefaultBatchSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(sessionStore);
        return SessionImport.ImportSessionToStoreAsync(sessionId, sessionStore, directory, includeSubagents, batchSize, ct);
    }
}

// Session metadata returned by ListSessionsAsync(). Contains only data extractable from stat +
// head/tail reads — no full JSONL parsing required (PY's `SDKSessionInfo`).
public sealed record SdkSessionInfo(
    string SessionId,
    string Summary,
    long LastModified,
    long? FileSize = null,
    string? CustomTitle = null,
    string? FirstPrompt = null,
    string? GitBranch = null,
    string? Cwd = null,
    string? Tag = null,
    long? CreatedAt = null);

// A user or assistant message from a session transcript, returned by GetSessionMessagesAsync() /
// GetSubagentMessagesAsync() (PY's `SessionMessage`).
public sealed record SessionMessage(
    string Type,
    string Uuid,
    string SessionId,
    JsonNode? Message,
    string? ParentToolUseId = null,
    string? ParentAgentId = null);

// Result of a fork operation.
public sealed record ForkSessionResult(string SessionId);
