using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Sessions;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// ClaudeSessions.*ViaStoreAsync, ported from PY.
[Property("TestKind", "Unit")]
public class SessionMutationsStoreTests
{
    // Implements ISessionStore only -- no ISessionStoreDeletion -- for the "deletion is a no-op"
    // contract.
    private sealed class NoDeleteStore(InMemorySessionStore inner) : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<JsonObject> entries, CancellationToken ct = default) => inner.AppendAsync(key, entries, ct);

        public Task<IReadOnlyList<JsonObject>?> LoadAsync(SessionKey key, CancellationToken ct = default) => inner.LoadAsync(key, ct);
    }

    private static JsonObject UserEntry(string uuid, string content, string? parentUuid = null) =>
        new()
        {
            ["type"] = "user",
            ["uuid"] = uuid,
            ["parentUuid"] = parentUuid,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };

    [Test]
    public async Task RenameSessionViaStoreAsync_AppendsCustomTitle_ReflectedInSummary()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [UserEntry("u1", "original prompt")]);

        await ClaudeSessions.RenameSessionViaStoreAsync(store, sessionId, "New Title", "some-dir");

        var info = await ClaudeSessions.GetSessionInfoFromStoreAsync(store, sessionId, "some-dir");
        await Assert.That(info!.CustomTitle).IsEqualTo("New Title");
        await Assert.That(info.Summary).IsEqualTo("New Title");
    }

    [Test]
    public async Task RenameSessionViaStoreAsync_EmptyTitle_Throws()
    {
        var store = new InMemorySessionStore();

        await Assert.That(() => ClaudeSessions.RenameSessionViaStoreAsync(store, Guid.NewGuid().ToString(), "   ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task RenameSessionViaStoreAsync_InvalidSessionId_Throws()
    {
        var store = new InMemorySessionStore();

        await Assert.That(() => ClaudeSessions.RenameSessionViaStoreAsync(store, "not-a-uuid", "Title")).Throws<ArgumentException>();
    }

    [Test]
    public async Task TagSessionViaStoreAsync_SetThenClear()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [UserEntry("u1", "prompt")]);

        await ClaudeSessions.TagSessionViaStoreAsync(store, sessionId, "urgent", "some-dir");
        var tagged = await ClaudeSessions.GetSessionInfoFromStoreAsync(store, sessionId, "some-dir");
        await Assert.That(tagged!.Tag).IsEqualTo("urgent");

        await ClaudeSessions.TagSessionViaStoreAsync(store, sessionId, null, "some-dir");
        var cleared = await ClaudeSessions.GetSessionInfoFromStoreAsync(store, sessionId, "some-dir");
        await Assert.That(cleared!.Tag).IsNull();
    }

    [Test]
    public async Task DeleteSessionViaStoreAsync_StoreWithDeletion_RemovesEntries()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        var key = new SessionKey(projectKey, sessionId);
        await store.AppendAsync(key, [UserEntry("u1", "prompt")]);

        await ClaudeSessions.DeleteSessionViaStoreAsync(store, sessionId, "some-dir");

        await Assert.That(await store.LoadAsync(key)).IsNull();
    }

    [Test]
    public async Task DeleteSessionViaStoreAsync_StoreWithoutDeletion_IsNoOp()
    {
        var inner = new InMemorySessionStore();
        var store = new NoDeleteStore(inner);
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        var key = new SessionKey(projectKey, sessionId);
        await store.AppendAsync(key, [UserEntry("u1", "prompt")]);

        await ClaudeSessions.DeleteSessionViaStoreAsync(store, sessionId, "some-dir");

        await Assert.That(await store.LoadAsync(key)).IsNotNull();
    }

    [Test]
    public async Task ForkSessionViaStoreAsync_MissingSession_Throws()
    {
        var store = new InMemorySessionStore();

        await Assert.That(async () => await ClaudeSessions.ForkSessionViaStoreAsync(store, Guid.NewGuid().ToString())).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task ForkSessionViaStoreAsync_CopiesTranscriptWithRemappedUuidsAndForkedFrom()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [UserEntry("u1", "hello")]);

        var result = await ClaudeSessions.ForkSessionViaStoreAsync(store, sessionId, "some-dir");

        await Assert.That(result.SessionId).IsNotEqualTo(sessionId);

        var forkedEntries = await store.LoadAsync(new SessionKey(projectKey, result.SessionId));
        await Assert.That(forkedEntries).IsNotNull();

        var forkedUserEntry = forkedEntries!.First(e => e["type"]!.ToString() == "user");
        await Assert.That(forkedUserEntry["uuid"]!.ToString()).IsNotEqualTo("u1");
        await Assert.That(forkedUserEntry["sessionId"]!.ToString()).IsEqualTo(result.SessionId);
        await Assert.That(forkedUserEntry["forkedFrom"]!["sessionId"]!.ToString()).IsEqualTo(sessionId);
        await Assert.That(forkedUserEntry["forkedFrom"]!["messageUuid"]!.ToString()).IsEqualTo("u1");
    }

    [Test]
    public async Task ForkSessionViaStoreAsync_UpToMessageId_SlicesTranscript()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        var firstUuid = Guid.NewGuid().ToString();
        var secondUuid = Guid.NewGuid().ToString();
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [
            UserEntry(firstUuid, "first"),
            UserEntry(secondUuid, "second", firstUuid),
        ]);

        var result = await ClaudeSessions.ForkSessionViaStoreAsync(store, sessionId, "some-dir", upToMessageId: firstUuid);

        var forkedEntries = await store.LoadAsync(new SessionKey(projectKey, result.SessionId));
        var userEntries = forkedEntries!.Where(e => e["type"]!.ToString() == "user").ToList();
        await Assert.That(userEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ForkSessionViaStoreAsync_ExplicitTitle_UsedVerbatim()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [UserEntry("u1", "hello")]);

        var result = await ClaudeSessions.ForkSessionViaStoreAsync(store, sessionId, "some-dir", title: "My Fork");

        var info = await ClaudeSessions.GetSessionInfoFromStoreAsync(store, result.SessionId, "some-dir");
        await Assert.That(info!.CustomTitle).IsEqualTo("My Fork");
    }
}
