using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Sessions;
using BD.Connectors.ClaudeCode.Sessions.Internal;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// SessionResume.MaterializeResumeSessionAsync / ApplyMaterializedOptions / BuildMirrorBatcher, ported
// from PY. Every test seeds options.Env["CLAUDE_CONFIG_DIR"] with an empty sandbox
// directory so CopyAuthFiles reads from a controlled, empty source (no real ~/.claude credentials
// leak into -- or are required by -- these tests, and the macOS-Keychain branch, which only runs
// when CLAUDE_CONFIG_DIR is unset, never activates).
[Property("TestKind", "Unit")]
public class SessionResumeTests
{
    private static JsonObject UserEntry(string uuid, string content, bool isSidechain = false)
    {
        var obj = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = uuid,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };
        if (isSidechain)
        {
            obj["isSidechain"] = true;
        }

        return obj;
    }

    private static string NewEmptySourceConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "claude-resume-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ClaudeAgentOptions BaseOptions(ISessionStore? store, string cwd) =>
        new()
        {
            SessionStore = store,
            Cwd = cwd,
            Env = new Dictionary<string, string> { [WireConstants.EnvVars.ClaudeConfigDir] = NewEmptySourceConfigDir() },
        };

    [Test]
    public async Task MaterializeResumeSessionAsync_NoSessionStore_ReturnsNull()
    {
        var options = new ClaudeAgentOptions { Resume = Guid.NewGuid().ToString() };

        var result = await SessionResume.MaterializeResumeSessionAsync(options);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MaterializeResumeSessionAsync_NoResumeNoContinue_ReturnsNull()
    {
        var options = BaseOptions(new InMemorySessionStore(), "cwd-a");

        var result = await SessionResume.MaterializeResumeSessionAsync(options);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MaterializeResumeSessionAsync_InvalidResumeUuid_ReturnsNull()
    {
        var options = BaseOptions(new InMemorySessionStore(), "cwd-a") with { Resume = "not-a-uuid" };

        var result = await SessionResume.MaterializeResumeSessionAsync(options);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MaterializeResumeSessionAsync_ResumeWithNoEntries_ReturnsNull()
    {
        var sessionId = Guid.NewGuid().ToString();
        var options = BaseOptions(new InMemorySessionStore(), "cwd-a") with { Resume = sessionId };

        var result = await SessionResume.MaterializeResumeSessionAsync(options);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MaterializeResumeSessionAsync_ExplicitResume_WritesJsonlToTempConfigDir()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var cwd = "cwd-explicit-resume";
        var projectKey = ClaudeSessions.ProjectKeyForDirectory(cwd);
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [UserEntry("u1", "hello")]);
        var options = BaseOptions(store, cwd) with { Resume = sessionId };

        var materialized = await SessionResume.MaterializeResumeSessionAsync(options);
        try
        {
            await Assert.That(materialized).IsNotNull();
            await Assert.That(materialized!.ResumeSessionId).IsEqualTo(sessionId);

            var jsonlPath = Path.Combine(materialized.ConfigDir, "projects", projectKey, $"{sessionId}.jsonl");
            await Assert.That(File.Exists(jsonlPath)).IsTrue();

            var lines = (await File.ReadAllTextAsync(jsonlPath)).TrimEnd('\n').Split('\n');
            await Assert.That(lines.Length).IsEqualTo(1);
            await Assert.That(JsonNode.Parse(lines[0])!["uuid"]!.ToString()).IsEqualTo("u1");
        }
        finally
        {
            if (materialized is not null)
            {
                await materialized.Cleanup();
            }
        }
    }

    [Test]
    public async Task MaterializeResumeSessionAsync_Cleanup_RemovesTempDir()
    {
        var store = new InMemorySessionStore();
        var sessionId = Guid.NewGuid().ToString();
        var cwd = "cwd-cleanup";
        var projectKey = ClaudeSessions.ProjectKeyForDirectory(cwd);
        await store.AppendAsync(new SessionKey(projectKey, sessionId), [UserEntry("u1", "hello")]);
        var options = BaseOptions(store, cwd) with { Resume = sessionId };

        var materialized = await SessionResume.MaterializeResumeSessionAsync(options);
        await Assert.That(Directory.Exists(materialized!.ConfigDir)).IsTrue();

        await materialized.Cleanup();

        await Assert.That(Directory.Exists(materialized.ConfigDir)).IsFalse();
    }

    [Test]
    public async Task MaterializeResumeSessionAsync_ContinueConversation_PicksNewestNonSidechainSession()
    {
        var store = new InMemorySessionStore();
        var cwd = "cwd-continue";
        var projectKey = ClaudeSessions.ProjectKeyForDirectory(cwd);
        var older = Guid.NewGuid().ToString();
        var newer = Guid.NewGuid().ToString();
        var sidechain = Guid.NewGuid().ToString();
        await store.AppendAsync(new SessionKey(projectKey, older), [UserEntry("u1", "older")]);
        await store.AppendAsync(new SessionKey(projectKey, newer), [UserEntry("u2", "newer")]);
        // Sidechain lands last (highest mtime) but must be skipped.
        await store.AppendAsync(new SessionKey(projectKey, sidechain), [UserEntry("u3", "sidechain", isSidechain: true)]);

        var options = BaseOptions(store, cwd) with { ContinueConversation = true };

        var materialized = await SessionResume.MaterializeResumeSessionAsync(options);
        try
        {
            await Assert.That(materialized).IsNotNull();
            await Assert.That(materialized!.ResumeSessionId).IsEqualTo(newer);
        }
        finally
        {
            if (materialized is not null)
            {
                await materialized.Cleanup();
            }
        }
    }

    [Test]
    public async Task MaterializeResumeSessionAsync_ContinueConversation_NoSessions_ReturnsNull()
    {
        var options = BaseOptions(new InMemorySessionStore(), "cwd-empty") with { ContinueConversation = true };

        var result = await SessionResume.MaterializeResumeSessionAsync(options);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ApplyMaterializedOptions_SetsEnvResumeAndClearsContinue()
    {
        var options = new ClaudeAgentOptions { ContinueConversation = true, Env = new Dictionary<string, string> { ["OTHER"] = "kept" } };
        var materialized = new SessionResume.MaterializedResume("C:\\temp\\claude-resume-x", "resolved-session-id", () => Task.CompletedTask);

        var result = SessionResume.ApplyMaterializedOptions(options, materialized);

        await Assert.That(result.Env["CLAUDE_CONFIG_DIR"]).IsEqualTo("C:\\temp\\claude-resume-x");
        await Assert.That(result.Env["OTHER"]).IsEqualTo("kept");
        await Assert.That(result.Resume).IsEqualTo("resolved-session-id");
        await Assert.That(result.ContinueConversation).IsFalse();
    }

    [Test]
    public async Task BuildMirrorBatcher_Eager_ZeroesPendingThresholds()
    {
        var store = new InMemorySessionStore();

        var batcher = SessionResume.BuildMirrorBatcher(store, null, null, (_, _) => Task.CompletedTask, SessionStoreFlushMode.Eager, null);

        // A single-entry enqueue must schedule an eager flush immediately since thresholds are zeroed.
        batcher.Enqueue(Path.Combine(SessionPaths.GetProjectsDir(), "proj", "session-1.jsonl"), new JsonArray([new JsonObject { ["type"] = "user", ["uuid"] = "u1" }]));

        await Assert.That(batcher.LastEagerFlushTask is not null).IsTrue();
    }

    [Test]
    public async Task BuildMirrorBatcher_Batched_DoesNotEagerFlushSingleEntry()
    {
        var store = new InMemorySessionStore();

        var batcher = SessionResume.BuildMirrorBatcher(store, null, null, (_, _) => Task.CompletedTask, SessionStoreFlushMode.Batched, null);

        batcher.Enqueue(Path.Combine(SessionPaths.GetProjectsDir(), "proj", "session-1.jsonl"), new JsonArray([new JsonObject { ["type"] = "user", ["uuid"] = "u1" }]));

        await Assert.That(batcher.LastEagerFlushTask is null).IsTrue();
    }
}
