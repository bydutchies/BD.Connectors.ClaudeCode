using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Permissions;
using BD.Connectors.ClaudeCode.Tests.Shared;

namespace BD.Connectors.ClaudeCode.Tests.E2E;

// Real, authenticated CLI (model "haiku"). Ports the relevant scenario from PY's e2e tests: a
// CanUseTool callback that refuses a Write outside an allowed directory must leave that file absent
// afterward. Never run in CI (RequiresAuthenticatedClaude skips without a logged-in CLI).
[Property("TestKind", "E2E")]
[RequiresAuthenticatedClaude]
public class QueryPermissionE2ETests
{
    [Test]
    public async Task QueryAsync_CanUseToolDeniesWriteOutsideAllowedDirectory_FileNeverCreated()
    {
        var sandboxDir = CreateSandboxDirectory();
        var deniedPath = Path.Combine(Path.GetTempPath(), $"claude-sdk-denied-{Guid.NewGuid():N}.txt");

        CanUseToolCallback canUseTool = (toolName, input, _) =>
        {
            if (toolName == "Write" && input["file_path"]?.GetValue<string>() == deniedPath)
            {
                return Task.FromResult<PermissionResult>(new PermissionResultDeny("Writing outside the sandbox is not allowed."));
            }

            return Task.FromResult<PermissionResult>(new PermissionResultAllow(input));
        };

        var options = new ClaudeAgentOptions
        {
            Model = "haiku",
            Cwd = sandboxDir,
            CanUseTool = canUseTool,
            PermissionMode = PermissionMode.Default,
            MaxTurns = 3,
        };

        try
        {
            await foreach (var _ in ClaudeAgent.QueryAsync(
                $"Use the Write tool to create the file at exactly this absolute path: {deniedPath}, with the content \"hello\". "
                + "Do not write anywhere else.",
                options))
            {
                // Draining is enough; only the resulting filesystem state is asserted below.
            }
        }
        finally
        {
            TryDeleteDirectory(sandboxDir);
        }

        await Assert.That(File.Exists(deniedPath)).IsFalse();
    }

    private static string CreateSandboxDirectory()
    {
        var dir = Path.Combine(
            RealClaudeTestSupport.ResolveRepositoryRootPath(),
            "tests",
            "BD.Connectors.ClaudeCode.Tests",
            ".sandbox",
            $"query-permission-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        // Best-effort sandbox cleanup only; see ClaudeCliSmokeTests for the same rationale.
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
