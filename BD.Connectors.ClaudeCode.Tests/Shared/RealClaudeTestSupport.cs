using System.Diagnostics;
using System.Text.Json;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Tests.Shared;

internal static class RealClaudeTestSupport
{
    private const string AuthCommandName = "auth";
    private const string StatusCommandName = "status";
    private const string ClaudeDirectoryName = ".claude";
    private const string ProjectsDirectoryName = "projects";
    private const string JsonLinesFileExtension = ".jsonl";
    private const string LoggedInPropertyName = "loggedIn";
    private const string CouldNotLocateRepositoryRootMessage = "Could not locate repository root from test execution directory.";
    private const int AuthenticationProbeTimeoutMilliseconds = 60000;
    private const string TerminateFailureMessagePrefix = "Failed to terminate timed-out Claude auth probe process: ";
    private static readonly Lazy<AuthenticationProbeResult> _cachedAuthenticationProbe = new(ProbeAuthentication);

    public static bool CanRunAuthenticatedTests()
    {
        if (!TryResolveExecutablePath(out var executablePath))
        {
            return false;
        }

        var probeResult = _cachedAuthenticationProbe.Value;
        return string.Equals(probeResult.ExecutablePath, executablePath, StringComparison.Ordinal)
               && probeResult.IsAuthenticated;
    }

    // CliSmoke tests need a real, spawnable CLI but no login; a resolvable-but-refused path (e.g. a
    // Windows npm claude.cmd shim with no native claude.exe anywhere) cannot actually run either.
    public static bool CanRunCliSmokeTests() => TryResolveExecutablePath(out _);

    public static string ResolveExecutablePathOrThrow()
    {
        if (!TryResolveExecutablePath(out var executablePath))
        {
            throw new InvalidOperationException("Claude Code CLI could not be resolved.");
        }

        return executablePath;
    }

    public static async Task<string?> FindPersistedSessionPathAsync(string sessionId, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var projectsPath = GetClaudeProjectsPath();
        if (projectsPath is null || !Directory.Exists(projectsPath))
        {
            return null;
        }

        var searchPattern = string.Concat(sessionId, JsonLinesFileExtension);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            var persistedPath = Directory
                .EnumerateFiles(projectsPath, searchPattern, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(persistedPath))
            {
                return persistedPath;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        return null;
    }

    public static string ResolveRepositoryRootPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, TestConstants.SolutionFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(CouldNotLocateRepositoryRootMessage);
    }

    private static bool TryResolveExecutablePath(out string executablePath)
    {
        try
        {
            var resolved = CliLocator.FindCli(null);
            CliLocator.RejectWindowsBatchCli(resolved, OperatingSystem.IsWindows());
            executablePath = resolved;
            return true;
        }
        catch (ClaudeSdkException)
        {
            // CliNotFoundException (nothing discoverable) or CliConnectionException (only a Windows
            // batch shim was found) both mean "no CLI these tests can actually run".
            executablePath = string.Empty;
            return false;
        }
    }

    private static string? GetClaudeProjectsPath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return null;
        }

        return Path.Combine(homeDirectory, ClaudeDirectoryName, ProjectsDirectoryName);
    }

    private static AuthenticationProbeResult ProbeAuthentication()
    {
        if (!TryResolveExecutablePath(out var executablePath))
        {
            return new AuthenticationProbeResult(null, false);
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(AuthCommandName);
        startInfo.ArgumentList.Add(StatusCommandName);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new AuthenticationProbeResult(executablePath, false);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(AuthenticationProbeTimeoutMilliseconds))
        {
            TryTerminate(process);
            return new AuthenticationProbeResult(executablePath, false);
        }

        Task.WaitAll(standardOutputTask, standardErrorTask);

        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();
        var combinedOutput = string.Concat(standardOutput, standardError);

        if (process.ExitCode != 0)
        {
            return new AuthenticationProbeResult(executablePath, false);
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(standardOutput) ? combinedOutput : standardOutput);
            var root = document.RootElement;
            var isAuthenticated = root.TryGetProperty(LoggedInPropertyName, out var loggedInElement)
                                  && loggedInElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                                  && loggedInElement.GetBoolean();
            return new AuthenticationProbeResult(executablePath, isAuthenticated);
        }
        catch (JsonException)
        {
            return new AuthenticationProbeResult(executablePath, false);
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine(string.Concat(TerminateFailureMessagePrefix, exception.Message));
        }
    }
}

internal sealed record AuthenticationProbeResult(string? ExecutablePath, bool IsAuthenticated);
