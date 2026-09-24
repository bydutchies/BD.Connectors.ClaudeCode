using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Sessions;
using BD.Connectors.ClaudeCode.Sessions.Internal;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// InMemorySessionStore + SessionStoreFileKeys.FilePathToSessionKey, ported from PY.
[Property("TestKind", "Unit")]
public class InMemorySessionStoreTests
{
    private static JsonObject Entry(string type, string uuid, params (string Key, string Value)[] fields)
    {
        var obj = new JsonObject { ["type"] = type, ["uuid"] = uuid };
        foreach (var (key, value) in fields)
        {
            obj[key] = value;
        }

        return obj;
    }

    [Test]
    public async Task AppendAndLoad_RoundTripsEntries()
    {
        var store = new InMemorySessionStore();
        var key = new SessionKey("proj", "session-1");

        await store.AppendAsync(key, [Entry("user", "u1")]);
        await store.AppendAsync(key, [Entry("assistant", "u2")]);

        var loaded = await store.LoadAsync(key);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Count).IsEqualTo(2);
        await Assert.That(loaded[0]["uuid"]!.ToString()).IsEqualTo("u1");
        await Assert.That(loaded[1]["uuid"]!.ToString()).IsEqualTo("u2");
    }

    [Test]
    public async Task LoadAsync_NeverWrittenKey_ReturnsNull()
    {
        var store = new InMemorySessionStore();

        var loaded = await store.LoadAsync(new SessionKey("proj", "missing"));

        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task ListSessionsAsync_SortsUnspecified_ButExcludesSubpathsAndOtherProjects()
    {
        var store = new InMemorySessionStore();
        await store.AppendAsync(new SessionKey("proj-a", "s1"), [Entry("user", "u1")]);
        await store.AppendAsync(new SessionKey("proj-a", "s2"), [Entry("user", "u2")]);
        await store.AppendAsync(new SessionKey("proj-a", "s2", "subagents/agent-x"), [Entry("user", "u3")]);
        await store.AppendAsync(new SessionKey("proj-b", "s3"), [Entry("user", "u4")]);

        var sessions = await store.ListSessionsAsync("proj-a");

        await Assert.That(sessions.Count).IsEqualTo(2);
        await Assert.That(sessions.Select(s => s.SessionId)).Contains("s1");
        await Assert.That(sessions.Select(s => s.SessionId)).Contains("s2");
    }

    [Test]
    public async Task ListSessionSummariesAsync_ScopedToProjectKey_ExcludesSubpathContributions()
    {
        var store = new InMemorySessionStore();
        var key = new SessionKey("proj-a", "s1");
        await store.AppendAsync(key, [Entry("user", "u1", ("message", string.Empty))]);
        await store.AppendAsync(new SessionKey("proj-a", "s1", "subagents/agent-x"), [Entry("user", "sub1")]);
        await store.AppendAsync(new SessionKey("proj-b", "s2"), [Entry("user", "u2")]);

        var summaries = await store.ListSessionSummariesAsync("proj-a");

        await Assert.That(summaries.Count).IsEqualTo(1);
        await Assert.That(summaries[0].SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task DeleteAsync_MainKey_CascadesToSubkeysAndSummary()
    {
        var store = new InMemorySessionStore();
        var mainKey = new SessionKey("proj", "s1");
        var subKey = new SessionKey("proj", "s1", "subagents/agent-x");
        await store.AppendAsync(mainKey, [Entry("user", "u1")]);
        await store.AppendAsync(subKey, [Entry("user", "sub1")]);

        await store.DeleteAsync(mainKey);

        await Assert.That(await store.LoadAsync(mainKey)).IsNull();
        await Assert.That(await store.LoadAsync(subKey)).IsNull();
        await Assert.That((await store.ListSessionSummariesAsync("proj")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteAsync_SubpathKey_RemovesOnlyThatEntry()
    {
        var store = new InMemorySessionStore();
        var mainKey = new SessionKey("proj", "s1");
        var subKey = new SessionKey("proj", "s1", "subagents/agent-x");
        await store.AppendAsync(mainKey, [Entry("user", "u1")]);
        await store.AppendAsync(subKey, [Entry("user", "sub1")]);

        await store.DeleteAsync(subKey);

        await Assert.That(await store.LoadAsync(mainKey)).IsNotNull();
        await Assert.That(await store.LoadAsync(subKey)).IsNull();
    }

    [Test]
    public async Task ListSubkeysAsync_ReturnsSubpathsUnderSession()
    {
        var store = new InMemorySessionStore();
        await store.AppendAsync(new SessionKey("proj", "s1"), [Entry("user", "u1")]);
        await store.AppendAsync(new SessionKey("proj", "s1", "subagents/agent-a"), [Entry("user", "sub1")]);
        await store.AppendAsync(new SessionKey("proj", "s1", "subagents/workflows/run-1/agent-b"), [Entry("user", "sub2")]);

        var subkeys = await store.ListSubkeysAsync(new SessionListSubkeysKey("proj", "s1"));

        await Assert.That(subkeys.Count).IsEqualTo(2);
        await Assert.That(subkeys).Contains("subagents/agent-a");
        await Assert.That(subkeys).Contains("subagents/workflows/run-1/agent-b");
    }

    [Test]
    public async Task Append_ProducesStrictlyIncreasingMtimes()
    {
        var store = new InMemorySessionStore();
        await store.AppendAsync(new SessionKey("proj", "s1"), [Entry("user", "u1")]);
        await store.AppendAsync(new SessionKey("proj", "s2"), [Entry("user", "u2")]);

        var sessions = (await store.ListSessionsAsync("proj")).ToDictionary(s => s.SessionId, s => s.Mtime);

        await Assert.That(sessions["s2"]).IsGreaterThan(sessions["s1"]);
    }

    [Test]
    public async Task TestHelpers_GetEntriesSizeAndClear()
    {
        var store = new InMemorySessionStore();
        await store.AppendAsync(new SessionKey("proj", "s1"), [Entry("user", "u1")]);
        await store.AppendAsync(new SessionKey("proj", "s2"), [Entry("user", "u2")]);

        await Assert.That(store.Size).IsEqualTo(2);
        await Assert.That(store.GetEntries(new SessionKey("proj", "s1")).Count).IsEqualTo(1);

        store.Clear();

        await Assert.That(store.Size).IsEqualTo(0);
        await Assert.That(await store.LoadAsync(new SessionKey("proj", "s1"))).IsNull();
    }

    // ---------------------------------------------------------------------
    // FilePathToSessionKey
    // ---------------------------------------------------------------------

    [Test]
    public async Task FilePathToSessionKey_MainTranscript_ParsesProjectAndSessionId()
    {
        var projectsDir = Path.Combine("C:", "claude", "projects");
        var filePath = Path.Combine(projectsDir, "proj-a", "session-1.jsonl");

        var key = SessionStoreFileKeys.FilePathToSessionKey(filePath, projectsDir);

        await Assert.That(key).IsNotNull();
        await Assert.That(key!.ProjectKey).IsEqualTo("proj-a");
        await Assert.That(key.SessionId).IsEqualTo("session-1");
        await Assert.That(key.Subpath).IsNull();
    }

    [Test]
    public async Task FilePathToSessionKey_SubagentTranscript_ParsesSubpath()
    {
        var projectsDir = Path.Combine("C:", "claude", "projects");
        var filePath = Path.Combine(projectsDir, "proj-a", "session-1", "subagents", "agent-x.jsonl");

        var key = SessionStoreFileKeys.FilePathToSessionKey(filePath, projectsDir);

        await Assert.That(key).IsNotNull();
        await Assert.That(key!.ProjectKey).IsEqualTo("proj-a");
        await Assert.That(key.SessionId).IsEqualTo("session-1");
        await Assert.That(key.Subpath).IsEqualTo("subagents/agent-x");
    }

    [Test]
    public async Task FilePathToSessionKey_NotUnderProjectsDir_ReturnsNull()
    {
        var projectsDir = Path.Combine("C:", "claude", "projects");
        var filePath = Path.Combine("C:", "elsewhere", "session-1.jsonl");

        var key = SessionStoreFileKeys.FilePathToSessionKey(filePath, projectsDir);

        await Assert.That(key).IsNull();
    }

    [Test]
    public async Task FilePathToSessionKey_TooFewSegments_ReturnsNull()
    {
        var projectsDir = Path.Combine("C:", "claude", "projects");
        var filePath = Path.Combine(projectsDir, "session-1.jsonl");

        var key = SessionStoreFileKeys.FilePathToSessionKey(filePath, projectsDir);

        await Assert.That(key).IsNull();
    }
}
