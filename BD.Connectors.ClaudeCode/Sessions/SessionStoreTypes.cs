using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Sessions;

// Identifies a session transcript or subagent transcript in a store (PY's `SessionKey`). Main
// transcripts have no Subpath; subagent transcripts include one like "subagents/agent-{id}" that
// mirrors the on-disk directory structure.
public sealed record SessionKey(string ProjectKey, string SessionId, string? Subpath = null);

// Entry returned by ISessionStoreListing.ListSessionsAsync (PY's `SessionStoreListEntry`).
public sealed record SessionStoreListEntry(string SessionId, long Mtime);

// Incrementally-maintained session summary (PY's `SessionSummaryEntry`). Obtained from
// SessionSummary.Fold inside ISessionStore.AppendAsync and persisted verbatim; Data is opaque
// SDK-owned state that stores must not interpret.
public sealed record SessionSummaryEntry(string SessionId, long Mtime, JsonObject Data);

// Key argument to ISessionStoreSubkeys.ListSubkeysAsync (no Subpath) (PY's `SessionListSubkeysKey`).
public sealed record SessionListSubkeysKey(string ProjectKey, string SessionId);

// Optional ISessionStore capability: list sessions for a project_key with modification times.
// PY: `SessionStore.list_sessions`, probed at runtime via `_store_implements`; C# stores implement
// this interface directly instead. Optional -- if unimplemented, ListSessionsFromStoreAsync raises.
public interface ISessionStoreListing
{
    Task<IReadOnlyList<SessionStoreListEntry>> ListSessionsAsync(string projectKey, CancellationToken cancellationToken = default);
}

// Optional ISessionStore capability: batch-return incrementally-maintained summaries for all sessions
// in a project_key (PY: `SessionStore.list_session_summaries`). Optional -- if unimplemented,
// ListSessionsFromStoreAsync falls back to ListSessionsAsync + per-session LoadAsync.
public interface ISessionStoreSummaries
{
    Task<IReadOnlyList<SessionSummaryEntry>> ListSessionSummariesAsync(string projectKey, CancellationToken cancellationToken = default);
}

// Optional ISessionStore capability: delete a session (PY: `SessionStore.delete`). Deleting a
// main-transcript key (no Subpath) must cascade to all subkeys under that session. Optional -- if
// unimplemented, deletion via DeleteSessionViaStoreAsync is a no-op.
public interface ISessionStoreDeletion
{
    Task DeleteAsync(SessionKey key, CancellationToken cancellationToken = default);
}

// Optional ISessionStore capability: list all subpath keys under a session, e.g. subagent transcripts
// (PY: `SessionStore.list_subkeys`). Optional -- if unimplemented, resume only materializes the main
// transcript, and ListSubagentsFromStoreAsync raises.
public interface ISessionStoreSubkeys
{
    Task<IReadOnlyList<string>> ListSubkeysAsync(SessionListSubkeysKey key, CancellationToken cancellationToken = default);
}
