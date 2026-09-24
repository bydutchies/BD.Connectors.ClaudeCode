using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Sessions;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// SessionStoreValidation.ValidateSessionStoreOptions, ported from PY.
[Property("TestKind", "Unit")]
public class SessionStoreValidationTests
{
    // A store implementing only ISessionStore (no listing capability), for the "insufficient
    // capability" scenarios.
    private sealed class MinimalStore : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<JsonObject> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<JsonObject>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JsonObject>?>(null);
    }

    [Test]
    public async Task NoSessionStore_NeverThrows()
    {
        await Assert.That(() => SessionStoreValidation.ValidateSessionStoreOptions(new ClaudeAgentOptions())).ThrowsNothing();
    }

    [Test]
    public async Task ContinueWithoutResume_StoreLacksListSessions_Throws()
    {
        var options = new ClaudeAgentOptions { SessionStore = new MinimalStore(), ContinueConversation = true };

        await Assert.That(() => SessionStoreValidation.ValidateSessionStoreOptions(options)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ContinueWithExplicitResume_StoreLacksListSessions_DoesNotThrow()
    {
        // Resume wins over continue, so list_sessions() is provably never called.
        var options = new ClaudeAgentOptions { SessionStore = new MinimalStore(), ContinueConversation = true, Resume = "some-session-id" };

        await Assert.That(() => SessionStoreValidation.ValidateSessionStoreOptions(options)).ThrowsNothing();
    }

    [Test]
    public async Task ContinueWithoutResume_StoreHasListSessions_DoesNotThrow()
    {
        var options = new ClaudeAgentOptions { SessionStore = new InMemorySessionStore(), ContinueConversation = true };

        await Assert.That(() => SessionStoreValidation.ValidateSessionStoreOptions(options)).ThrowsNothing();
    }

    [Test]
    public async Task SessionStoreWithFileCheckpointing_Throws()
    {
        var options = new ClaudeAgentOptions { SessionStore = new InMemorySessionStore(), EnableFileCheckpointing = true };

        await Assert.That(() => SessionStoreValidation.ValidateSessionStoreOptions(options)).Throws<ArgumentException>();
    }

    [Test]
    public async Task NullOptions_Throws()
    {
        await Assert.That(() => SessionStoreValidation.ValidateSessionStoreOptions(null!)).Throws<ArgumentNullException>();
    }
}
