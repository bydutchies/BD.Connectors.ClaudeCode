using System.Text;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Sessions;
using BD.Connectors.ClaudeCode.Sessions.Internal;
using BD.Connectors.ClaudeCode.Tests.Shared;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// ClaudeSessions.ImportSessionToStoreAsync, ported from PY. Replays a local on-disk session
// transcript into an ISessionStore -- the inverse of SessionResume.
[Property("TestKind", "Unit")]
[NotInParallel]
public class SessionImportTests
{
    [Test]
    public async Task ImportSessionToStoreAsync_InvalidSessionId_Throws()
    {
        await Assert.That(async () => await ClaudeSessions.ImportSessionToStoreAsync("not-a-uuid", new InMemorySessionStore())).Throws<ArgumentException>();
    }

    [Test]
    public async Task ImportSessionToStoreAsync_MissingSession_Throws()
    {
        await Assert.That(async () => await ClaudeSessions.ImportSessionToStoreAsync(Guid.NewGuid().ToString(), new InMemorySessionStore()))
            .Throws<FileNotFoundException>();
    }

    [Test]
    public async Task ImportSessionToStoreAsync_MainTranscript_AppendsUnderOnDiskProjectDirName()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/import-project");
            var (sessionId, _) = WriteSession(projectDir, [
                UserLine("u1", "hello"),
                AssistantLine("a1", "hi there", "u1"),
            ]);

            var store = new InMemorySessionStore();
            await ClaudeSessions.ImportSessionToStoreAsync(sessionId, store, "/repo/import-project");

            var onDiskProjectDirName = Path.GetFileName(projectDir);
            var loaded = await store.LoadAsync(new SessionKey(onDiskProjectDirName, sessionId));

            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Count).IsEqualTo(2);
            await Assert.That(loaded[0]["uuid"]!.ToString()).IsEqualTo("u1");
            await Assert.That(loaded[1]["uuid"]!.ToString()).IsEqualTo("a1");
        });
    }

    [Test]
    public async Task ImportSessionToStoreAsync_SmallBatchSize_StillImportsAllEntriesInOrder()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/import-batches");
            var (sessionId, _) = WriteSession(projectDir, [
                UserLine("u1", "one"),
                AssistantLine("a1", "two", "u1"),
                UserLine("u2", "three", "a1"),
                AssistantLine("a2", "four", "u2"),
            ]);

            var store = new InMemorySessionStore();
            await ClaudeSessions.ImportSessionToStoreAsync(sessionId, store, "/repo/import-batches", batchSize: 1);

            var onDiskProjectDirName = Path.GetFileName(projectDir);
            var loaded = await store.LoadAsync(new SessionKey(onDiskProjectDirName, sessionId));

            await Assert.That(loaded!.Count).IsEqualTo(4);
            await Assert.That(string.Join(",", loaded.Select(e => e["uuid"]!.ToString()))).IsEqualTo("u1,a1,u2,a2");
        });
    }

    [Test]
    public async Task ImportSessionToStoreAsync_IncludesSubagentTranscriptsAndMetadataSidecar()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/import-subagents");
            var (sessionId, _) = WriteSession(projectDir, [UserLine("u1", "hello")]);

            var subagentsDir = Path.Combine(projectDir, sessionId, "subagents");
            Directory.CreateDirectory(subagentsDir);
            var agentPath = Path.Combine(subagentsDir, "agent-abc.jsonl");
            File.WriteAllText(agentPath, UserLine("su1", "sub prompt") + "\n", new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(subagentsDir, "agent-abc.meta.json"),
                new JsonObject { ["toolUseId"] = "toolu_1", ["parentAgentId"] = "parent" }.ToJsonString(),
                new UTF8Encoding(false));

            var store = new InMemorySessionStore();
            await ClaudeSessions.ImportSessionToStoreAsync(sessionId, store, "/repo/import-subagents");

            var onDiskProjectDirName = Path.GetFileName(projectDir);
            var subKey = new SessionKey(onDiskProjectDirName, sessionId, "subagents/agent-abc");
            var loaded = await store.LoadAsync(subKey);

            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Any(e => e["type"]!.ToString() == "agent_metadata" && e["toolUseId"]!.ToString() == "toolu_1")).IsTrue();
            await Assert.That(loaded.Any(e => e["uuid"]?.ToString() == "su1")).IsTrue();
        });
    }

    [Test]
    public async Task ImportSessionToStoreAsync_ExcludeSubagents_SkipsSubagentTranscripts()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/import-no-subagents");
            var (sessionId, _) = WriteSession(projectDir, [UserLine("u1", "hello")]);

            var subagentsDir = Path.Combine(projectDir, sessionId, "subagents");
            Directory.CreateDirectory(subagentsDir);
            File.WriteAllText(Path.Combine(subagentsDir, "agent-abc.jsonl"), UserLine("su1", "sub prompt") + "\n", new UTF8Encoding(false));

            var store = new InMemorySessionStore();
            await ClaudeSessions.ImportSessionToStoreAsync(sessionId, store, "/repo/import-no-subagents", includeSubagents: false);

            var onDiskProjectDirName = Path.GetFileName(projectDir);
            var subKey = new SessionKey(onDiskProjectDirName, sessionId, "subagents/agent-abc");
            await Assert.That(await store.LoadAsync(subKey)).IsNull();
        });
    }

    // ---------------------------------------------------------------------
    // Fixture helpers (same pattern as ClaudeSessionsTests).
    // ---------------------------------------------------------------------

    private static async Task WithSandboxAsync(Func<string, Task> body)
    {
        var sandboxRoot = Path.Combine(
            RealClaudeTestSupport.ResolveRepositoryRootPath(), "tests", TestConstants.SandboxDirectoryName, "session-import-" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(sandboxRoot, ".claude");
        Directory.CreateDirectory(Path.Combine(configDir, "projects"));

        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", configDir);
        try
        {
            await body(configDir).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
            try
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort sandbox cleanup.
            }
        }
    }

    private static string MakeProjectDir(string configDir, string projectPath)
    {
        var sanitized = SessionPaths.SanitizePath(SessionPaths.CanonicalizePath(projectPath));
        var projectDir = Path.Combine(configDir, "projects", sanitized);
        Directory.CreateDirectory(projectDir);
        return projectDir;
    }

    private static (string SessionId, string FilePath) WriteSession(string projectDir, IEnumerable<string> jsonLines, string? sessionId = null)
    {
        var id = sessionId ?? Guid.NewGuid().ToString();
        var path = Path.Combine(projectDir, $"{id}.jsonl");
        File.WriteAllText(path, string.Join("\n", jsonLines) + "\n", new UTF8Encoding(false));
        return (id, path);
    }

    private static string UserLine(string uuid, string content, string? parentUuid = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = uuid,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };
        if (parentUuid is not null)
        {
            obj["parentUuid"] = parentUuid;
        }

        return obj.ToJsonString();
    }

    private static string AssistantLine(string uuid, string content, string? parentUuid = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "assistant",
            ["uuid"] = uuid,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
        };
        if (parentUuid is not null)
        {
            obj["parentUuid"] = parentUuid;
        }

        return obj.ToJsonString();
    }
}
