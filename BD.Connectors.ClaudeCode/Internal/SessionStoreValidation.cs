using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Sessions;

namespace BD.Connectors.ClaudeCode.Internal;

// Pre-flight validation for ClaudeAgentOptions.SessionStore combinations. Ported from PY.
internal static class SessionStoreValidation
{
    // Raise ArgumentException (PY: ValueError) for invalid session_store option combinations. Called
    // before subprocess spawn so misconfiguration fails fast instead of surfacing as a confusing
    // runtime error mid-session.
    public static void ValidateSessionStoreOptions(ClaudeAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var store = options.SessionStore;
        if (store is null)
        {
            return;
        }

        // When resume is explicitly set, ListSessionsAsync() is provably never called (resume wins
        // over continue), so a minimal store is fine. PY probes for the method at runtime
        // (`_store_implements`); C# checks the capability interface directly instead.
        if (options.ContinueConversation && options.Resume is null && store is not ISessionStoreListing)
        {
            throw new ArgumentException("continue_conversation with session_store requires the store to implement list_sessions()");
        }

        if (options.EnableFileCheckpointing)
        {
            throw new ArgumentException(
                "session_store cannot be combined with enable_file_checkpointing (checkpoints are local-disk only and would diverge from the mirrored transcript)");
        }
    }
}
