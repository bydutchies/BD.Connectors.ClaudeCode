using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// Ported from PY (path sanitization, config/project directories, UUID validation, and git worktree
// detection).
internal static partial class SessionPaths
{
    // Upper bound on a single sanitized filesystem path component.
    public const int MaxSanitizedLength = 200;

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    public static string? ValidateUuid(string maybeUuid) => UuidRegex().IsMatch(maybeUuid) ? maybeUuid : null;

    // 32-bit integer hash to base36, matching the CLI's directory naming. Iterates per Unicode
    // codepoint (Rune), not per UTF-16 char, so surrogate-pair characters hash
    // the same way Python's per-codepoint `for ch in s` loop does.
    public static string SimpleHash(string s)
    {
        var h = 0L;
        foreach (var rune in s.EnumerateRunes())
        {
            h = unchecked((h << 5) - h + rune.Value);
            h &= 0xFFFFFFFFL;
            if (h >= 0x80000000L)
            {
                h -= 0x100000000L;
            }
        }

        h = Math.Abs(h);
        if (h == 0)
        {
            return "0";
        }

        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder();
        var n = h;
        while (n > 0)
        {
            sb.Insert(0, digits[(int)(n % 36)]);
            n /= 36;
        }

        return sb.ToString();
    }

    // Replaces every non-ASCII-alnum codepoint with '-'. Runs per-Rune so a
    // supplementary-plane character (surrogate pair) becomes exactly one '-', matching Python's
    // per-codepoint semantics; the result is therefore always pure ASCII, so plain .Length afterwards
    // equals Python's len() (codepoint count) on `sanitized`.
    public static string SanitizePath(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value < 128 && char.IsAsciiLetterOrDigit((char)rune.Value))
            {
                sb.Append((char)rune.Value);
            }
            else
            {
                sb.Append('-');
            }
        }

        var sanitized = sb.ToString();
        if (sanitized.Length <= MaxSanitizedLength)
        {
            return sanitized;
        }

        var hash = SimpleHash(name);
        return string.Concat(sanitized.AsSpan(0, MaxSanitizedLength), "-", hash);
    }

    public static string GetClaudeConfigHomeDir()
    {
        var configDir = Environment.GetEnvironmentVariable(WireConstants.EnvVars.ClaudeConfigDir);
        if (!string.IsNullOrEmpty(configDir))
        {
            return configDir.Normalize(NormalizationForm.FormC);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude").Normalize(NormalizationForm.FormC);
    }

    // `envOverride` is consulted before the real process environment so callers that pass
    // CLAUDE_CONFIG_DIR to the subprocess via ClaudeAgentOptions.Env resolve the same directory the
    // subprocess will write to (matches PY).
    public static string GetProjectsDir(IReadOnlyDictionary<string, string>? envOverride = null)
    {
        if (envOverride is not null
            && envOverride.TryGetValue(WireConstants.EnvVars.ClaudeConfigDir, out var overrideDir)
            && !string.IsNullOrEmpty(overrideDir))
        {
            return Path.Combine(overrideDir.Normalize(NormalizationForm.FormC), "projects");
        }

        return Path.Combine(GetClaudeConfigHomeDir(), "projects");
    }

    public static string GetProjectDir(string projectPath) => Path.Combine(GetProjectsDir(), SanitizePath(projectPath));

    public static string CanonicalizePath(string d)
    {
        try
        {
            return PathUtil.RealPath(d);
        }
        catch (Exception)
        {
            return d.Normalize(NormalizationForm.FormC);
        }
    }

    // Tolerates hash mismatches for long paths (>MaxSanitizedLength), matching PY.
    public static string? FindProjectDir(string projectPath)
    {
        var exact = GetProjectDir(projectPath);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        var sanitized = SanitizePath(projectPath);
        if (sanitized.Length <= MaxSanitizedLength)
        {
            return null;
        }

        var prefix = sanitized[..MaxSanitizedLength];
        var projectsDir = GetProjectsDir();
        try
        {
            foreach (var entry in Directory.EnumerateDirectories(projectsDir))
            {
                if (Path.GetFileName(entry).StartsWith(prefix + "-", StringComparison.Ordinal))
                {
                    return entry;
                }
            }
        }
        catch (Exception)
        {
            // Best-effort scan, matches PY's `except OSError: pass` — no logger reachable from this
            // static PY-mirroring API (ClaudeSessions takes no ILogger; see PORTING_STATUS.md).
        }

        return null;
    }

    // Returns absolute worktree paths for the git repo containing cwd, or [] if git is unavailable,
    // times out, or cwd is not in a repo (matches PY).
    public static IReadOnlyList<string> GetWorktreePaths(string cwd)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("worktree");
            startInfo.ArgumentList.Add("list");
            startInfo.ArgumentList.Add("--porcelain");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                TryKillProcess(process);
                return [];
            }

            Task.WaitAll(stdoutTask, stderrTask);
            var stdout = stdoutTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 || string.IsNullOrEmpty(stdout))
            {
                return [];
            }

            var paths = new List<string>();
            foreach (var line in stdout.Split('\n'))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    paths.Add(line["worktree ".Length..].Normalize(NormalizationForm.FormC));
                }
            }

            return paths;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup for a timed-out `git worktree list` probe; no logger reachable here.
        }
    }
}
