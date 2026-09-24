using System.Diagnostics;
using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Tests.Shared;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Tests.CliSmoke;

// Real `claude` CLI, no login required. Skipped entirely when no spawnable CLI is discoverable
// (RequiresClaudeCliAttribute) -- never in CI before ci.yml installs the CLI, but safe to leave
// enabled unconditionally.
[Property("TestKind", "CliSmoke")]
[RequiresClaudeCli]
public class ClaudeCliSmokeTests
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    [Test]
    public async Task CliLocator_ResolvesAnExistingFile()
    {
        var path = RealClaudeTestSupport.ResolveExecutablePathOrThrow();

        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task Cli_VersionFlag_OutputParsesAsSemanticVersion()
    {
        var output = await RunCliAsync("-v");

        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(output.Trim(), @"^[0-9]+\.[0-9]+\.[0-9]+")).IsTrue();
    }

    [Test]
    public async Task Cli_Help_MentionsStreamJson()
    {
        var output = await RunCliAsync("--help");

        await Assert.That(output).Contains("stream-json");
    }

    // F8.3: ClaudeCli.GetVersionAsync() against the real, locally installed CLI -- no login required.
    [Test]
    public async Task ClaudeCli_GetVersionAsync_ReturnsSemanticVersion()
    {
        var metadata = await ClaudeCli.GetVersionAsync();

        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(metadata.InstalledVersion, @"^[0-9]+\.[0-9]+\.[0-9]+")).IsTrue();
    }

    [Test]
    public async Task Transport_ConnectAndInitializeHandshake_GetsControlResponseOrAuthError_WithoutHanging()
    {
        var sandboxDir = CreateSandboxDirectory();
        var options = new ClaudeAgentOptions
        {
            Env = new Dictionary<string, string>
            {
                // Isolated config/home so this never touches (or depends on) the developer's real
                // Claude Code login state; an unauthenticated CLI still answers the initialize
                // handshake (or reports an auth error), which is all this test needs.
                ["CLAUDE_CONFIG_DIR"] = sandboxDir,
                ["HOME"] = sandboxDir,
                ["USERPROFILE"] = sandboxDir,
            },
        };

        var transport = new SubprocessCliTransport(options);
        try
        {
            using var cts = new CancellationTokenSource(HandshakeTimeout);

            await transport.ConnectAsync(cts.Token);
            await transport.WriteAsync(
                "{\"type\":\"control_request\",\"request_id\":\"req_1_smoke\",\"request\":{\"subtype\":\"initialize\",\"hooks\":null}}\n",
                cts.Token);

            JsonObject? handshakeMessage = null;
            await foreach (var message in transport.ReadMessagesAsync(cts.Token))
            {
                var type = message["type"]?.GetValue<string>();
                if (type is "control_response" or "result")
                {
                    handshakeMessage = message;
                    break;
                }
            }

            await Assert.That(handshakeMessage).IsNotNull();
        }
        finally
        {
            await transport.CloseAsync();
            TryDeleteDirectory(sandboxDir);
        }
    }

    // Proves there are no orphan processes after DisposeAsync (checked via Process.GetProcessById).
    // ConnectAsync() with no prompt only drives the initialize handshake (like
    // Transport_ConnectAndInitializeHandshake... above), so it succeeds -- or fails with an
    // auth-related ClaudeSdkException -- without ever reaching a model call; either way,
    // ConnectCoreAsync/DisposeAsync must still tear the subprocess down.
    [Test]
    public async Task ClaudeSdkClient_ConnectThenDispose_LeavesNoOrphanProcess()
    {
        var sandboxDir = CreateSandboxDirectory();
        var options = new ClaudeAgentOptions
        {
            Env = new Dictionary<string, string>
            {
                ["CLAUDE_CONFIG_DIR"] = sandboxDir,
                ["HOME"] = sandboxDir,
                ["USERPROFILE"] = sandboxDir,
            },
        };

        var cliPath = RealClaudeTestSupport.ResolveExecutablePathOrThrow();
        var processName = Path.GetFileNameWithoutExtension(cliPath);
        var before = GetProcessIdsRunningFrom(processName, cliPath);

        try
        {
            var client = new ClaudeSdkClient(options);
            try
            {
                using var cts = new CancellationTokenSource(HandshakeTimeout);
                await client.ConnectAsync(cancellationToken: cts.Token);
            }
            catch (ClaudeSdkException)
            {
                // Auth-related initialize failures are expected against an unauthenticated sandbox
                // CLI; ConnectAsync already tore the subprocess down before rethrowing (see
                // ClaudeSdkClient.ConnectCoreAsync). What matters here is that no process survives.
            }

            await client.DisposeAsync();

            var deadline = DateTime.UtcNow.Add(TimeSpan.FromSeconds(15));
            HashSet<int> leaked;
            do
            {
                leaked = GetProcessIdsRunningFrom(processName, cliPath);
                leaked.ExceptWith(before);
                if (leaked.Count == 0)
                {
                    break;
                }

                await Task.Delay(200);
            }
            while (DateTime.UtcNow < deadline);

            await Assert.That(leaked.Count).IsEqualTo(0);
        }
        finally
        {
            TryDeleteDirectory(sandboxDir);
        }
    }

    // Process.GetProcessesByName() matches by bare name only, case-insensitively -- on a real dev
    // machine that also runs the Claude desktop app (an Electron app whose main/renderer/GPU/utility
    // helper processes are *all* literally named "Claude"), that false-matches unrelated processes
    // that come and go for reasons that have nothing to do with this SDK, making a bare name diff
    // flaky. Narrow the match to processes whose actual executable is the resolved CLI path.
    private static HashSet<int> GetProcessIdsRunningFrom(string processName, string cliPath)
    {
        var matches = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, cliPath, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(process.Id);
                    }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // A process we can't introspect (exited mid-query, or a different security
                    // context, e.g. another app's helper process) is by definition not one of ours.
                }
            }
        }

        return matches;
    }

    private static async Task<string> RunCliAsync(string argument)
    {
        var startInfo = new ProcessStartInfo(RealClaudeTestSupport.ResolveExecutablePathOrThrow())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return stdout.Length > 0 ? stdout : stderr;
    }

    private static string CreateSandboxDirectory()
    {
        var dir = Path.Combine(
            RealClaudeTestSupport.ResolveRepositoryRootPath(),
            "tests",
            "BD.Connectors.ClaudeCode.Tests",
            ".sandbox",
            $"cli-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        // Best-effort sandbox cleanup only; a lingering .sandbox/ directory from a failed delete
        // (e.g. an antivirus scanner still holding a handle) is harmless test-output litter, not a
        // functional failure worth surfacing.
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
