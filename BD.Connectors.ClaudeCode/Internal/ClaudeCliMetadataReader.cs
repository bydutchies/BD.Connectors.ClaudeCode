using System.Diagnostics;
using System.Globalization;

namespace BD.Connectors.ClaudeCode.Internal;

// Async port of CS `ClaudeCliMetadataReader`'s pure version-parsing/comparison functions and process
// calls. Backs the public ClaudeCli.GetVersionAsync()/GetUpdateStatusAsync().
internal static class ClaudeCliMetadataReader
{
    private const string GitTagPrefix = "refs/tags/v";

    public static async Task<string> ReadInstalledVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start Claude Code executable '{executablePath}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Claude Code CLI exited with code {process.ExitCode} while reading version. {details}".Trim());
        }

        return ParseInstalledVersion(stdout);
    }

    internal static string ParseInstalledVersion(string versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            throw new InvalidOperationException("Claude Code version output is empty.");
        }

        var firstToken = versionOutput.Trim()
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstToken))
        {
            throw new InvalidOperationException($"Failed to parse Claude Code version output: '{versionOutput}'.");
        }

        return firstToken;
    }

    // Probes the latest published release tag via `git ls-remote --tags --refs` against the public
    // anthropics/claude-code repository. Never throws -- any failure (missing git, no network, non-zero
    // exit) is reported as (null, errorMessage).
    public static async Task<(string? LatestVersion, string? ErrorMessage)> ProbeLatestPublishedVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var gitExecutable = OperatingSystem.IsWindows() ? "git.exe" : "git";
            var startInfo = new ProcessStartInfo(gitExecutable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("ls-remote");
            startInfo.ArgumentList.Add("--tags");
            startInfo.ArgumentList.Add("--refs");
            startInfo.ArgumentList.Add("https://github.com/anthropics/claude-code.git");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (null, "Failed to start git process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return (null, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            }

            return (ParseLatestPublishedVersion(stdout), null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, e.Message);
        }
    }

    internal static string? ParseLatestPublishedVersion(string gitOutput)
    {
        if (string.IsNullOrWhiteSpace(gitOutput))
        {
            return null;
        }

        SemanticVersion? best = null;
        foreach (var rawLine in gitOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tagToken = rawLine.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(tagToken) || !tagToken.StartsWith(GitTagPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var versionText = tagToken[GitTagPrefix.Length..];
            if (!TryParseSemanticVersion(versionText, out var candidate))
            {
                continue;
            }

            if (best is null || CompareSemanticVersion(candidate, best.Value) > 0)
            {
                best = candidate;
            }
        }

        return best?.ToNormalizedString();
    }

    internal static bool IsNewerVersion(string latestVersion, string installedVersion)
    {
        if (!TryParseSemanticVersion(latestVersion, out var latest) || !TryParseSemanticVersion(installedVersion, out var installed))
        {
            return false;
        }

        return CompareSemanticVersion(latest, installed) > 0;
    }

    internal static bool TryParseSemanticVersion(string value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var plusIndex = value.IndexOf('+');
        var withoutBuildMetadata = plusIndex >= 0 ? value[..plusIndex] : value;

        var dashIndex = withoutBuildMetadata.IndexOf('-');
        var numericPortion = dashIndex >= 0 ? withoutBuildMetadata[..dashIndex] : withoutBuildMetadata;
        var preReleasePortion = dashIndex >= 0 ? withoutBuildMetadata[(dashIndex + 1)..] : null;

        var segments = numericPortion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is < 1 or > 3)
        {
            return false;
        }

        if (!int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        if (segments.Length >= 2 && !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        var patch = 0;
        if (segments.Length == 3 && !int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preReleasePortion);
        return true;
    }

    internal static int CompareSemanticVersion(SemanticVersion left, SemanticVersion right)
    {
        var majorComparison = left.Major.CompareTo(right.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = left.Minor.CompareTo(right.Minor);
        if (minorComparison != 0)
        {
            return minorComparison;
        }

        var patchComparison = left.Patch.CompareTo(right.Patch);
        if (patchComparison != 0)
        {
            return patchComparison;
        }

        if (string.IsNullOrWhiteSpace(left.PreRelease) && string.IsNullOrWhiteSpace(right.PreRelease))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(left.PreRelease))
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(right.PreRelease))
        {
            return -1;
        }

        return ComparePreRelease(left.PreRelease, right.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftIdentifiers = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rightIdentifiers = right.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var count = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);

        for (var index = 0; index < count; index++)
        {
            var comparison = ComparePreReleaseIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftIsNumeric = ulong.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightIsNumeric = ulong.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

        if (leftIsNumeric && rightIsNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (leftIsNumeric)
        {
            return -1;
        }

        if (rightIsNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease)
    {
        public string ToNormalizedString()
        {
            var version = string.Create(
                CultureInfo.InvariantCulture,
                $"{Major}.{Minor}.{Patch}");
            return string.IsNullOrWhiteSpace(PreRelease) ? version : $"{version}-{PreRelease}";
        }
    }
}
