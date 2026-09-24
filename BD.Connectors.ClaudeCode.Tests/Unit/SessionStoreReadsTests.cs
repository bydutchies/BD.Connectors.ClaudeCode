using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Sessions;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// ClaudeSessions.*FromStoreAsync, ported from PY.
[Property("TestKind", "Unit")]
public class SessionStoreReadsTests
{
    // Implements only ISessionStoreListing (no summaries fast path, no subkeys/deletion), to exercise
    // the per-session-load slow path of ListSessionsFromStoreAsync.
    private sealed class ListingOnlyStore(InMemorySessionStore inner) : ISessionStore, ISessionStoreListing
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<JsonObject> entries, CancellationToken ct = default) => inner.AppendAsync(key, entries, ct);

        public Task<IReadOnlyList<JsonObject>?> LoadAsync(SessionKey key, CancellationToken ct = default) => inner.LoadAsync(key, ct);

        public Task<IReadOnlyList<SessionStoreListEntry>> ListSessionsAsync(string projectKey, CancellationToken ct = default) =>
            inner.ListSessionsAsync(projectKey, ct);
    }

    private sealed class BareStore : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<JsonObject> entries, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<JsonObject>?> LoadAsync(SessionKey key, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JsonObject>?>(null);
    }

    private static JsonObject UserEntry(string uuid, string content, string? parentUuid = null, string? sessionId = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = uuid,
            ["sessionId"] = sessionId,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };
        if (parentUuid is not null)
        {
            obj["parentUuid"] = parentUuid;
        }

        return obj;
    }

    private static JsonObject AssistantEntry(string uuid, string content, string? parentUuid, string? sessionId = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "assistant",
            ["uuid"] = uuid,
            ["sessionId"] = sessionId,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
        };
        if (parentUuid is not null)
        {
            obj["parentUuid"] = parentUuid;
        }

        return obj;
    }

    [Test]
    public async Task ListSessionsFromStoreAsync_FastPath_SortsByMtimeDescending()
    {
        var store = new InMemorySessionStore();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        await store.AppendAsync(new SessionKey(projectKey, "s1"), [UserEntry("u1", "first session")]);
        await store.AppendAsync(new SessionKey(projectKey, "s2"), [UserEntry("u2", "second session")]);

        var sessions = await ClaudeSessions.ListSessionsFromStoreAsync(store, "some-dir");

        await Assert.That(sessions.Count).IsEqualTo(2);
        await Assert.That(sessions[0].SessionId).IsEqualTo("s2");
        await Assert.That(sessions[0].Summary).IsEqualTo("second session");
        await Assert.That(sessions[1].SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task ListSessionsFromStoreAsync_SlowPath_ListingOnlyStore_DerivesViaLoad()
    {
        var inner = new InMemorySessionStore();
        var store = new ListingOnlyStore(inner);
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        await store.AppendAsync(new SessionKey(projectKey, "s1"), [UserEntry("u1", "hello from slow path")]);

        var sessions = await ClaudeSessions.ListSessionsFromStoreAsync(store, "some-dir");

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].Summary).IsEqualTo("hello from slow path");
    }

    [Test]
    public async Task ListSessionsFromStoreAsync_NoListingNoSummaries_Throws()
    {
        var store = new BareStore();

        await Assert.That(async () => await ClaudeSessions.ListSessionsFromStoreAsync(store, "some-dir")).Throws<ArgumentException>();
    }

    [Test]
    public async Task ListSessionsFromStoreAsync_LimitAndOffset_Paginate()
    {
        var store = new InMemorySessionStore();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("paged-dir");
        await store.AppendAsync(new SessionKey(projectKey, "s1"), [UserEntry("u1", "one")]);
        await Task.Delay(5);
        await store.AppendAsync(new SessionKey(projectKey, "s2"), [UserEntry("u2", "two")]);
        await Task.Delay(5);
        await store.AppendAsync(new SessionKey(projectKey, "s3"), [UserEntry("u3", "three")]);

        var page = await ClaudeSessions.ListSessionsFromStoreAsync(store, "paged-dir", limit: 1, offset: 1);

        await Assert.That(page.Count).IsEqualTo(1);
        await Assert.That(page[0].SessionId).IsEqualTo("s2");
    }

    [Test]
    public async Task GetSessionInfoFromStoreAsync_InvalidUuid_ReturnsNull()
    {
        var store = new InMemorySessionStore();

        var info = await ClaudeSessions.GetSessionInfoFromStoreAsync(store, "not-a-uuid");

        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task GetSessionInfoFromStoreAsync_MissingSession_ReturnsNull()
    {
        var store = new InMemorySessionStore();

        var info = await ClaudeSessions.GetSessionInfoFromStoreAsync(store, Guid.NewGuid().ToString(), "some-dir");

        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task GetSessionInfoFromStoreAsync_FoundSession_ParsesSummary()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [UserEntry("u1", "the prompt")]);

        var info = await ClaudeSessions.GetSessionInfoFromStoreAsync(store, sessionId, "some-dir");

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Summary).IsEqualTo("the prompt");
    }

    [Test]
    public async Task GetSessionMessagesFromStoreAsync_BuildsChainFromParentUuid()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");
        var u1 = "u1";
        var a1 = "a1";
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [
            UserEntry(u1, "question", sessionId: sessionId),
            AssistantEntry(a1, "answer", u1, sessionId),
        ]);

        var messages = await ClaudeSessions.GetSessionMessagesFromStoreAsync(store, sessionId, "some-dir");

        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages[0].Type).IsEqualTo("user");
        await Assert.That(messages[1].Type).IsEqualTo("assistant");
    }

    [Test]
    public async Task GetSessionMessagesFromStoreAsync_MissingSession_ReturnsEmpty()
    {
        var store = new InMemorySessionStore();

        var messages = await ClaudeSessions.GetSessionMessagesFromStoreAsync(store, Guid.NewGuid().ToString(), "some-dir");

        await Assert.That(messages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListSubagentsFromStoreAsync_StoreWithoutSubkeys_Throws()
    {
        var store = new BareStore();

        await Assert.That(async () => await ClaudeSessions.ListSubagentsFromStoreAsync(store, Guid.NewGuid().ToString())).Throws<ArgumentException>();
    }

    [Test]
    public async Task ListAndGetSubagentMessagesFromStoreAsync_RoundTrip()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var projectKey = ClaudeSessions.ProjectKeyForDirectory("some-dir");

        var agentMeta = new JsonObject
        {
            ["type"] = "agent_metadata",
            ["toolUseId"] = "toolu_1",
            ["parentAgentId"] = "parent-agent",
        };
        var subKey = new SessionKey(projectKey, sessionId, "subagents/agent-abc");
        await store.AppendAsync(subKey, [
            agentMeta,
            UserEntry("su1", "sub question", sessionId: sessionId),
            AssistantEntry("sa1", "sub answer", "su1", sessionId),
        ]);

        var ids = await ClaudeSessions.ListSubagentsFromStoreAsync(store, sessionId, "some-dir");
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0]).IsEqualTo("abc");

        var messages = await ClaudeSessions.GetSubagentMessagesFromStoreAsync(store, sessionId, "abc", "some-dir");
        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages[0].ParentToolUseId).IsEqualTo("toolu_1");
        await Assert.That(messages[0].ParentAgentId).IsEqualTo("parent-agent");
    }

    [Test]
    public async Task GetSubagentMessagesFromStoreAsync_UnknownAgent_ReturnsEmpty()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();

        var messages = await ClaudeSessions.GetSubagentMessagesFromStoreAsync(store, sessionId, "does-not-exist", "some-dir");

        await Assert.That(messages.Count).IsEqualTo(0);
    }
}
