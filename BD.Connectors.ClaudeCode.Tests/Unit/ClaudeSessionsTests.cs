using System.Text;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Sessions;
using BD.Connectors.ClaudeCode.Sessions.Internal;
using BD.Connectors.ClaudeCode.Tests.Shared;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Integration tests for the public Sessions API, exercised end-to-end against real files under a
// temporary CLAUDE_CONFIG_DIR. CLAUDE_CONFIG_DIR is process-wide state, so every test in this class
// runs sequentially (NotInParallel).
[Property("TestKind", "Unit")]
[NotInParallel]
public class ClaudeSessionsTests
{
    [Test]
    public async Task ListSessionsAsync_SortsByLastModifiedDescending_AndPaginates()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/project-a");

            var (oldest, oldestPath) = WriteSession(projectDir, [
                UserLine(NewUuid(), "s", "oldest prompt"),
                TailLine("summary", ("customTitle", "Oldest")),
            ]);
            var (middle, middlePath) = WriteSession(projectDir, [
                UserLine(NewUuid(), "s", "middle prompt"),
                TailLine("summary", ("customTitle", "Middle")),
            ]);
            var (newest, newestPath) = WriteSession(projectDir, [
                UserLine(NewUuid(), "s", "newest prompt"),
                TailLine("summary", ("customTitle", "Newest")),
            ]);

            var baseTime = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(oldestPath, baseTime);
            File.SetLastWriteTimeUtc(middlePath, baseTime.AddMinutes(1));
            File.SetLastWriteTimeUtc(newestPath, baseTime.AddMinutes(2));

            var all = await ClaudeSessions.ListSessionsAsync("/repo/project-a", includeWorktrees: false);

            await Assert.That(all.Count).IsEqualTo(3);
            await Assert.That(all[0].SessionId).IsEqualTo(newest);
            await Assert.That(all[1].SessionId).IsEqualTo(middle);
            await Assert.That(all[2].SessionId).IsEqualTo(oldest);

            var page = await ClaudeSessions.ListSessionsAsync("/repo/project-a", limit: 1, offset: 1, includeWorktrees: false);

            await Assert.That(page.Count).IsEqualTo(1);
            await Assert.That(page[0].SessionId).IsEqualTo(middle);
        });
    }

    [Test]
    public async Task ListSessionsAsync_IncludeWorktreesDefault_DegradesGracefullyOutsideGitRepo()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/no-git");
            WriteSession(projectDir, [UserLine(NewUuid(), "s", "hello")]);

            // The sandbox directory is not a git repo, so `git worktree list` fails/returns nothing;
            // ListSessionsAsync must still fall back to a plain single-directory scan.
            var result = await ClaudeSessions.ListSessionsAsync("/repo/no-git");

            await Assert.That(result.Count).IsEqualTo(1);
        });
    }

    [Test]
    public async Task GetSessionInfoAsync_NoDirectory_SearchesAllProjectDirectories()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/somewhere");
            var (sessionId, _) = WriteSession(projectDir, [
                UserLine(NewUuid(), "s", "hi"),
                TailLine("summary", ("customTitle", "Findable")),
            ]);

            var info = await ClaudeSessions.GetSessionInfoAsync(sessionId);

            await Assert.That(info).IsNotNull();
            await Assert.That(info!.Summary).IsEqualTo("Findable");
        });
    }

    [Test]
    public async Task GetSessionInfoAsync_UnknownSessionId_ReturnsNull()
    {
        await WithSandboxAsync(async _ =>
        {
            var info = await ClaudeSessions.GetSessionInfoAsync(Guid.NewGuid().ToString());

            await Assert.That(info).IsNull();
        });
    }

    [Test]
    public async Task GetSessionMessagesAsync_BuildsChainAndExcludesSidechain()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/chain");
            var u1 = NewUuid();
            var a1 = NewUuid();
            var u2 = NewUuid();
            var a2 = NewUuid();
            var side = NewUuid();

            var (sessionId, _) = WriteSession(projectDir, [
                UserLine(u1, "s", "first question"),
                AssistantLine(a1, "s", "first answer", parentUuid: u1),
                UserLine(u2, "s", "second question", parentUuid: a1),
                AssistantLine(a2, "s", "second answer", parentUuid: u2),
                UserLine(side, "s", "unrelated sidechain", isSidechain: true),
            ]);

            var messages = await ClaudeSessions.GetSessionMessagesAsync(sessionId, "/repo/chain");

            await Assert.That(messages.Count).IsEqualTo(4);
            await Assert.That(messages[0].Uuid).IsEqualTo(u1);
            await Assert.That(messages[1].Uuid).IsEqualTo(a1);
            await Assert.That(messages[2].Uuid).IsEqualTo(u2);
            await Assert.That(messages[3].Uuid).IsEqualTo(a2);
            await Assert.That(messages.Any(m => m.Uuid == side)).IsFalse();
        });
    }

    [Test]
    public async Task GetSessionMessagesAsync_Pagination()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/page");
            var u1 = NewUuid();
            var a1 = NewUuid();
            var u2 = NewUuid();
            var a2 = NewUuid();

            var (sessionId, _) = WriteSession(projectDir, [
                UserLine(u1, "s", "q1"),
                AssistantLine(a1, "s", "a1", parentUuid: u1),
                UserLine(u2, "s", "q2", parentUuid: a1),
                AssistantLine(a2, "s", "a2", parentUuid: u2),
            ]);

            var page = await ClaudeSessions.GetSessionMessagesAsync(sessionId, "/repo/page", limit: 2, offset: 1);

            await Assert.That(page.Count).IsEqualTo(2);
            await Assert.That(page[0].Uuid).IsEqualTo(a1);
            await Assert.That(page[1].Uuid).IsEqualTo(u2);
        });
    }

    [Test]
    public async Task Subagents_ListAndGetMessages_ReadSidecarMetadata()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/subagents");
            var (sessionId, _) = WriteSession(projectDir, [UserLine(NewUuid(), "s", "main")]);

            var subagentsDir = Path.Combine(projectDir, sessionId, "subagents");
            Directory.CreateDirectory(subagentsDir);

            var agentId = "agent-abc";
            var su = NewUuid();
            var sa = NewUuid();
            File.WriteAllText(
                Path.Combine(subagentsDir, $"agent-{agentId}.jsonl"),
                string.Join("\n", [
                    UserLine(su, "s", "subagent question"),
                    AssistantLine(sa, "s", "subagent answer", parentUuid: su),
                ]) + "\n",
                new UTF8Encoding(false));

            File.WriteAllText(
                Path.Combine(subagentsDir, $"agent-{agentId}.meta.json"),
                "{\"toolUseId\":\"tool-use-1\"}",
                new UTF8Encoding(false));

            var agentIds = await ClaudeSessions.ListSubagentsAsync(sessionId, "/repo/subagents");
            await Assert.That(agentIds.Count).IsEqualTo(1);
            await Assert.That(agentIds[0]).IsEqualTo(agentId);

            var messages = await ClaudeSessions.GetSubagentMessagesAsync(sessionId, agentId, "/repo/subagents");
            await Assert.That(messages.Count).IsEqualTo(2);
            await Assert.That(messages[0].ParentToolUseId).IsEqualTo("tool-use-1");
            await Assert.That(messages[0].ParentAgentId).IsNull();
        });
    }

    [Test]
    public async Task RenameSessionAsync_LastAppendWins()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/rename");
            var (sessionId, _) = WriteSession(projectDir, [UserLine(NewUuid(), "s", "hi")]);

            await ClaudeSessions.RenameSessionAsync(sessionId, "First Title", "/repo/rename");
            await ClaudeSessions.RenameSessionAsync(sessionId, "  Second Title  ", "/repo/rename");

            var info = await ClaudeSessions.GetSessionInfoAsync(sessionId, "/repo/rename");

            await Assert.That(info!.Summary).IsEqualTo("Second Title");
        });
    }

    [Test]
    public async Task RenameSessionAsync_UnknownSession_Throws()
    {
        await WithSandboxAsync(async _ =>
        {
            await Assert.That(async () => await ClaudeSessions.RenameSessionAsync(Guid.NewGuid().ToString(), "x")).Throws<FileNotFoundException>();
        });
    }

    [Test]
    public async Task TagSessionAsync_SetThenClear()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/tag");
            var (sessionId, _) = WriteSession(projectDir, [UserLine(NewUuid(), "s", "hi")]);

            await ClaudeSessions.TagSessionAsync(sessionId, "experiment", "/repo/tag");
            var tagged = await ClaudeSessions.GetSessionInfoAsync(sessionId, "/repo/tag");
            await Assert.That(tagged!.Tag).IsEqualTo("experiment");

            await ClaudeSessions.TagSessionAsync(sessionId, null, "/repo/tag");
            var cleared = await ClaudeSessions.GetSessionInfoAsync(sessionId, "/repo/tag");
            await Assert.That(cleared!.Tag).IsNull();
        });
    }

    [Test]
    public async Task TagSessionAsync_WhitespaceOnlyTag_Throws()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/tag-invalid");
            var (sessionId, _) = WriteSession(projectDir, [UserLine(NewUuid(), "s", "hi")]);

            await Assert.That(async () => await ClaudeSessions.TagSessionAsync(sessionId, "   ", "/repo/tag-invalid")).Throws<ArgumentException>();
        });
    }

    [Test]
    public async Task DeleteSessionAsync_RemovesFileAndSubagentSiblingDirectory()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/delete");
            var (sessionId, filePath) = WriteSession(projectDir, [UserLine(NewUuid(), "s", "hi")]);
            var sideDir = Path.Combine(projectDir, sessionId);
            Directory.CreateDirectory(Path.Combine(sideDir, "subagents"));

            await ClaudeSessions.DeleteSessionAsync(sessionId, "/repo/delete");

            await Assert.That(File.Exists(filePath)).IsFalse();
            await Assert.That(Directory.Exists(sideDir)).IsFalse();
        });
    }

    [Test]
    public async Task DeleteSessionAsync_UnknownSession_Throws()
    {
        await WithSandboxAsync(async _ =>
        {
            await Assert.That(async () => await ClaudeSessions.DeleteSessionAsync(Guid.NewGuid().ToString())).Throws<FileNotFoundException>();
        });
    }

    [Test]
    public async Task ForkSessionAsync_CopiesTranscriptWithNewUuidsAndDerivedTitle()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/fork");
            var u1 = NewUuid();
            var a1 = NewUuid();
            var (sessionId, _) = WriteSession(projectDir, [
                UserLine(u1, "s", "fork me"),
                AssistantLine(a1, "s", "sure", parentUuid: u1),
                TailLine("summary", ("customTitle", "Original")),
            ]);

            var result = await ClaudeSessions.ForkSessionAsync(sessionId, "/repo/fork");

            await Assert.That(result.SessionId).IsNotEqualTo(sessionId);
            await Assert.That(SessionPaths.ValidateUuid(result.SessionId)).IsNotNull();

            var forkedMessages = await ClaudeSessions.GetSessionMessagesAsync(result.SessionId, "/repo/fork");
            await Assert.That(forkedMessages.Count).IsEqualTo(2);
            await Assert.That(forkedMessages[0].Uuid).IsNotEqualTo(u1);
            await Assert.That(forkedMessages[0].SessionId).IsEqualTo(result.SessionId);

            var forkedInfo = await ClaudeSessions.GetSessionInfoAsync(result.SessionId, "/repo/fork");
            await Assert.That(forkedInfo!.Summary).IsEqualTo("Original (fork)");
        });
    }

    [Test]
    public async Task ForkSessionAsync_UpToMessageId_TruncatesTranscript()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/fork-truncate");
            var u1 = NewUuid();
            var a1 = NewUuid();
            var u2 = NewUuid();
            var a2 = NewUuid();
            var (sessionId, _) = WriteSession(projectDir, [
                UserLine(u1, "s", "q1"),
                AssistantLine(a1, "s", "a1", parentUuid: u1),
                UserLine(u2, "s", "q2", parentUuid: a1),
                AssistantLine(a2, "s", "a2", parentUuid: u2),
            ]);

            var result = await ClaudeSessions.ForkSessionAsync(sessionId, "/repo/fork-truncate", upToMessageId: a1);

            var forkedMessages = await ClaudeSessions.GetSessionMessagesAsync(result.SessionId, "/repo/fork-truncate");

            await Assert.That(forkedMessages.Count).IsEqualTo(2);
        });
    }

    [Test]
    public async Task ForkSessionAsync_ExplicitTitle_SkipsForkSuffix()
    {
        await WithSandboxAsync(async configDir =>
        {
            var projectDir = MakeProjectDir(configDir, "/repo/fork-title");
            var (sessionId, _) = WriteSession(projectDir, [UserLine(NewUuid(), "s", "hi")]);

            var result = await ClaudeSessions.ForkSessionAsync(sessionId, "/repo/fork-title", title: "Custom Fork Title");

            var forkedInfo = await ClaudeSessions.GetSessionInfoAsync(result.SessionId, "/repo/fork-title");

            await Assert.That(forkedInfo!.Summary).IsEqualTo("Custom Fork Title");
        });
    }

    [Test]
    public async Task ForkSessionAsync_InvalidSessionId_Throws()
    {
        await WithSandboxAsync(async _ =>
        {
            await Assert.That(async () => await ClaudeSessions.ForkSessionAsync("not-a-uuid")).Throws<ArgumentException>();
        });
    }

    [Test]
    public async Task ProjectKeyForDirectory_MatchesSanitizePathOfCanonicalizedDirectory()
    {
        var dir = Path.GetTempPath();

        var result = ClaudeSessions.ProjectKeyForDirectory(dir);

        await Assert.That(result).IsEqualTo(SessionPaths.SanitizePath(SessionPaths.CanonicalizePath(dir)));
    }

    // ---------------------------------------------------------------------
    // Fixture helpers
    // ---------------------------------------------------------------------

    private static async Task WithSandboxAsync(Func<string, Task> body)
    {
        var sandboxRoot = Path.Combine(
            RealClaudeTestSupport.ResolveRepositoryRootPath(), "tests", TestConstants.SandboxDirectoryName, "sessions-" + Guid.NewGuid().ToString("N"));
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

    // Sessions/ClaudeSessions.cs always canonicalizes `directory` (realpath + NFC) before sanitizing
    // it into a project directory name — mirror that here so a POSIX-looking test path like
    // "/repo/project-a" lands in the same directory ListSessionsAsync etc. will actually look in
    // (Path.GetFullPath resolves a leading "/" against the current drive on Windows).
    private static string MakeProjectDir(string configDir, string projectPath)
    {
        var sanitized = SessionPaths.SanitizePath(SessionPaths.CanonicalizePath(projectPath));
        var projectDir = Path.Combine(configDir, "projects", sanitized);
        Directory.CreateDirectory(projectDir);
        return projectDir;
    }

    private static (string SessionId, string FilePath) WriteSession(string projectDir, IEnumerable<string> jsonLines, string? sessionId = null)
    {
        var id = sessionId ?? NewUuid();
        var path = Path.Combine(projectDir, $"{id}.jsonl");
        File.WriteAllText(path, string.Join("\n", jsonLines) + "\n", new UTF8Encoding(false));
        return (id, path);
    }

    private static string NewUuid() => Guid.NewGuid().ToString();

    private static string UserLine(string uuid, string sessionId, string content, string? parentUuid = null, bool isSidechain = false, bool isMeta = false)
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

        if (isSidechain)
        {
            obj["isSidechain"] = true;
        }

        if (isMeta)
        {
            obj["isMeta"] = true;
        }

        return obj.ToJsonString();
    }

    private static string AssistantLine(string uuid, string sessionId, string content, string? parentUuid = null)
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

        return obj.ToJsonString();
    }

    private static string TailLine(string type, params (string Key, string Value)[] fields)
    {
        var obj = new JsonObject { ["type"] = type };
        foreach (var (key, value) in fields)
        {
            obj[key] = value;
        }

        return obj.ToJsonString();
    }
}
