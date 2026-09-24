using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Sessions;

// Adapter for mirroring session transcripts to external storage (PY's `SessionStore` Protocol). The
// subprocess still writes to local disk (set CLAUDE_CONFIG_DIR to a temp dir for an ephemeral local
// copy); the adapter receives a secondary copy.
//
// The SDK never deletes from the store unless the caller calls a `*ViaStoreAsync` delete with
// ISessionStoreDeletion implemented. Retention is the adapter's responsibility.
//
// Only AppendAsync/LoadAsync are required. PY models the remaining methods (list_sessions,
// list_session_summaries, delete, list_subkeys) as optional Protocol members with a runtime
// `_store_implements` probe (isinstance of the overriding function vs. the Protocol default). C# has
// no equivalent duck-typing probe, so each optional capability is its own interface
// (ISessionStoreListing/ISessionStoreSummaries/ISessionStoreDeletion/ISessionStoreSubkeys, see
// SessionStoreTypes.cs) that a store implements only if it supports that capability; call sites use
// `store is IWhatever` instead of PY's `_store_implements`.
public interface ISessionStore
{
    // Mirror a batch of transcript entries. Called AFTER the subprocess's local write succeeds --
    // durability is already guaranteed locally. Most entries carry a stable "uuid" that adapters
    // should treat as an idempotency key.
    Task AppendAsync(SessionKey key, IReadOnlyList<JsonObject> entries, CancellationToken cancellationToken = default);

    // Load a full session for resume. Return null for a key that was never written.
    Task<IReadOnlyList<JsonObject>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default);
}
