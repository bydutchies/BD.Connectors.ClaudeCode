using System.Globalization;
using BD.Connectors.ClaudeCode.Errors;

namespace BD.Connectors.ClaudeCode.Transport;

// CLI discovery, ported from PY (no bundled-CLI lookup: this SDK does not bundle the CLI binary).
public static class CliLocator
{
    // cmd.exe metacharacters (plus the quote character cmd.exe uses to toggle its quoting state, and
    // "!", which expands like "%" when delayed expansion is enabled). ProcessStartInfo.ArgumentList
    // quotes arguments for the MSVCRT argv rules only, not for cmd.exe, so in a whitespace-free
    // argument these characters reach a cmd.exe command line verbatim.
    private const string CmdExeMetacharacters = "&|<>^%!\"";

    private static readonly string[] _defaultWindowsPathExt =
    [
        ".COM", ".EXE", ".BAT", ".CMD", ".VBS", ".VBE", ".JS", ".JSE", ".WSF", ".WSH", ".MSC",
    ];

    // Resolves the CLI using live process/environment state.
    public static string FindCli(string? cliPath)
    {
        return FindCli(
            cliPath,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            File.Exists);
    }

    // Pure, testable core: no direct environment/filesystem access besides `fileExists`.
    internal static string FindCli(
        string? cliPath,
        bool isWindows,
        string? pathVar,
        string? pathExt,
        string homeDir,
        Func<string, bool> fileExists)
    {
        if (!string.IsNullOrEmpty(cliPath))
        {
            return cliPath;
        }

        string? whichHit = null;
        var cli = Which("claude", isWindows, pathVar, pathExt, fileExists);
        if (cli is not null)
        {
            if (!isWindows || IsWindowsNativeExe(cli))
            {
                return cli;
            }

            // Windows resolved something CreateProcess cannot run directly as the CLI: npm's
            // claude.cmd shim (which ConnectAsync refuses to spawn) or an extensionless wrapper
            // script. Prefer any discoverable native executable, and keep this hit only as the
            // last resort so a shim-only machine still gets the explanatory batch-script refusal.
            var exe = Which("claude.exe", isWindows, pathVar, pathExt, fileExists);
            if (exe is not null && IsWindowsNativeExe(exe))
            {
                return exe;
            }

            whichHit = cli;
        }

        IReadOnlyList<string> locations = isWindows
            ? [CombineHome(isWindows, homeDir, ".local", "bin", "claude.exe")]
            : [
                CombineHome(isWindows, homeDir, ".npm-global", "bin", "claude"),
                "/usr/local/bin/claude",
                CombineHome(isWindows, homeDir, ".local", "bin", "claude"),
                CombineHome(isWindows, homeDir, "node_modules", ".bin", "claude"),
                CombineHome(isWindows, homeDir, ".yarn", "bin", "claude"),
                CombineHome(isWindows, homeDir, ".claude", "local", "claude"),
            ];

        foreach (var location in locations)
        {
            if (fileExists(location))
            {
                return location;
            }
        }

        if (whichHit is not null)
        {
            // No native executable was discoverable anywhere: return the original which() hit so
            // ConnectAsync raises the batch-script refusal (with its remediation) for a shim, or the
            // spawn error for a wrapper script, rather than a bare not-found error.
            return whichHit;
        }

        if (isWindows)
        {
            throw new CliNotFoundException(
                "Claude Code not found. Install the native claude.exe with (PowerShell):\n"
                + "  irm https://claude.ai/install.ps1 | iex\n"
                + "\nOr provide the path to a claude.exe via ClaudeAgentOptions:\n"
                + "  new ClaudeAgentOptions { CliPath = \"C:\\\\path\\\\to\\\\claude.exe\" }\n"
                + "\n(npm install -g @anthropic-ai/claude-code produces a claude.cmd shim, which this SDK refuses to run on Windows.)");
        }

        throw new CliNotFoundException(
            "Claude Code not found. Install with:\n"
            + "  npm install -g @anthropic-ai/claude-code\n"
            + "\nIf already installed locally, try:\n"
            + "  export PATH=\"$HOME/node_modules/.bin:$PATH\"\n"
            + "\nOr provide the path via ClaudeAgentOptions:\n"
            + "  new ClaudeAgentOptions { CliPath = \"/path/to/claude\" }");
    }

    // Whether cliPath's final path component names an image CreateProcess runs directly
    // (.exe / .com), used only to decide which discovery result to prefer. Not a security gate:
    // every returned path still passes RejectWindowsBatchCli in ConnectAsync.
    internal static bool IsWindowsNativeExe(string cliPath)
    {
        var name = cliPath.Replace('\\', '/').Split('/')[^1];
        var trimmed = name.TrimEnd('.', ' ').ToLowerInvariant();
        return trimmed.EndsWith(".exe", StringComparison.Ordinal) || trimmed.EndsWith(".com", StringComparison.Ordinal);
    }

    // Whether cliPath names a .bat/.cmd batch script on Windows. Always false off Windows.
    // See RejectWindowsBatchCli for why spawning such a script is refused.
    //
    // Classifies EVERY path component (not only the final one) and, within a component, every
    // ":"-separated segment (an NTFS stream spec or a drive prefix rides in the same component), each
    // with trailing dots/spaces stripped (the normalization Windows applies at path resolution). This
    // closes the whole "clever path spelling" class outright: no real claude.exe lives beneath a
    // directory named like a batch file, so over-refusing on a batch-named component costs nothing
    // legitimate.
    internal static bool IsWindowsBatchCli(string cliPath, bool isWindows)
    {
        if (!isWindows)
        {
            return false;
        }

        foreach (var component in cliPath.Replace('\\', '/').Split('/'))
        {
            foreach (var segment in component.Split(':'))
            {
                var trimmed = segment.TrimEnd('.', ' ').ToLowerInvariant();
                if (trimmed.EndsWith(".bat", StringComparison.Ordinal) || trimmed.EndsWith(".cmd", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Refuse to execute a .bat/.cmd script as the CLI on Windows. Windows has no shebang mechanism:
    // CreateProcess runs batch scripts by silently rewriting the spawn into a "cmd.exe /c" invocation,
    // and cmd.exe re-parses the whole command line at execution time. Reliable escaping for cmd.exe
    // does not exist, so spawning a batch script with runtime-provided arguments (e.g. a --resume
    // value) cannot be made safe. Refusing is the same remediation Node.js shipped for this
    // vulnerability class (CVE-2024-27980, "BatBadBut").
    internal static void RejectWindowsBatchCli(string cliPath, bool isWindows)
    {
        if (!IsWindowsBatchCli(cliPath, isWindows))
        {
            return;
        }

        throw new CliConnectionException(
            $"Refusing to execute batch script {PyRepr(cliPath)}: Windows runs .bat/.cmd files via cmd.exe, "
            + "which can execute commands injected through CLI arguments, and no reliable escaping for cmd.exe "
            + "exists. Use a native claude executable instead: install Claude Code natively "
            + "(irm https://claude.ai/install.ps1 | iex), or point ClaudeAgentOptions.CliPath at a claude.exe.");
    }

    // Defense in depth for Windows: with batch-script spawning refused, these characters are
    // harmless (ArgumentList quotes correctly for native executables). Rejected anyway so that
    // resume / session_id / resume_session_at / resume_drops_turn values, which applications commonly
    // take from external input, stay inert even if a cmd.exe hop is ever reintroduced.
    internal static void RejectWindowsCmdMetacharacters(string optionName, string value, bool isWindows)
    {
        if (!isWindows)
        {
            return;
        }

        var bad = value
            .Where(c => CmdExeMetacharacters.Contains(c) || c is '\r' or '\n')
            .Distinct()
            .OrderBy(c => c)
            .ToArray();
        if (bad.Length == 0)
        {
            return;
        }

        throw new ArgumentException(
            $"{optionName} value {PyRepr(value)} contains characters that are unsafe to pass on a Windows "
            + $"command line: {PyReprList(bad)}");
    }

    internal static string PyRepr(string value)
    {
        var useDoubleQuote = value.Contains('\'') && !value.Contains('"');
        var quote = useDoubleQuote ? '"' : '\'';
        var result = new System.Text.StringBuilder();
        result.Append(quote);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    result.Append("\\\\");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\r':
                    result.Append("\\r");
                    break;
                case '\t':
                    result.Append("\\t");
                    break;
                default:
                    if (ch == quote)
                    {
                        result.Append('\\').Append(ch);
                    }
                    else if (ch < 0x20 || ch == 0x7f)
                    {
                        result.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        result.Append(ch);
                    }

                    break;
            }
        }

        result.Append(quote);
        return result.ToString();
    }

    private static string PyReprList(IEnumerable<char> chars) => "[" + string.Join(", ", chars.Select(c => PyRepr(c.ToString()))) + "]";

    // Mirrors Python's shutil.which(name) on the given platform: on Windows, if `name` does not
    // already end with a PATHEXT extension, every PATHEXT candidate is tried (PATH-directory-major,
    // PATHEXT-order-minor); otherwise `name` is tried as-is only.
    private static string? Which(string name, bool isWindows, string? pathVariable, string? pathExt, Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        IReadOnlyList<string> candidates;
        if (isWindows)
        {
            var extensions = SplitPathExt(pathExt);
            var alreadyMatches = extensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            candidates = alreadyMatches ? [name] : [.. extensions.Select(ext => name + ext)];
        }
        else
        {
            candidates = [name];
        }

        foreach (var dir in SplitPathVariable(pathVariable, isWindows))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = JoinPath(isWindows, dir, candidate);
                if (fileExists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    // Deliberately not System.IO.Path (Path.Combine / Path.PathSeparator follow the CURRENT host OS,
    // not the `isWindows` parameter under test): these pure functions must behave the same on any CI
    // runner regardless of which platform they execute on.
    private static string JoinPath(bool isWindows, string directory, string fileName)
    {
        var separator = isWindows ? '\\' : '/';
        return directory.Length > 0 && (directory[^1] == '\\' || directory[^1] == '/')
            ? directory + fileName
            : directory + separator + fileName;
    }

    private static string[] SplitPathExt(string? pathExt)
    {
        return string.IsNullOrEmpty(pathExt)
            ? _defaultWindowsPathExt
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> SplitPathVariable(string pathVariable, bool isWindows)
    {
        var separator = isWindows ? ';' : ':';
        foreach (var raw in pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = raw.Trim('"');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    private static string CombineHome(bool isWindows, string homeDir, params string[] segments)
    {
        var result = homeDir.TrimEnd('\\', '/');
        foreach (var segment in segments)
        {
            result = JoinPath(isWindows, result, segment);
        }

        return result;
    }
}
