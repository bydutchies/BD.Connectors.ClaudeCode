namespace BD.Connectors.ClaudeCode;

// CLI metadata/update-check -- extra convenience not present in PY (ported from CS
// ClaudeCliMetadataReader as an async API). PY has no equivalent: the CLI version is only ever
// checked internally against a minimum (Transport/SubprocessCliTransport's own
// CheckClaudeVersionAsync), never surfaced to callers.

// The installed `claude` CLI's version, as reported by `claude --version`.
public sealed record ClaudeCliMetadata(string InstalledVersion);

// Result of comparing the installed CLI version against the latest published release tag.
public sealed record ClaudeCliUpdateStatus(
    string InstalledVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? Message)
{
    public const string UpdateCommand = "claude update";
}

// Async CLI metadata/update-check API. Ported from CS `ClaudeCliMetadataReader` (sync process calls)
// as async.
public static class ClaudeCli
{
    // Reads the installed CLI's version via `claude --version`. `cliPath` defaults to the same
    // discovery `CliLocator.FindCli` uses for the subprocess transport.
    public static async Task<ClaudeCliMetadata> GetVersionAsync(string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var resolvedPath = Transport.CliLocator.FindCli(cliPath);
        var installedVersion = await Internal.ClaudeCliMetadataReader.ReadInstalledVersionAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        return new ClaudeCliMetadata(installedVersion);
    }

    // Reads the installed version and probes the latest published release tag via
    // `git ls-remote --tags --refs` against the public `anthropics/claude-code` repository (requires a
    // `git` executable on PATH; network access). Never throws for a failed probe -- a failure is
    // reported via `ClaudeCliUpdateStatus.Message` with `LatestVersion` and `UpdateAvailable = false`.
    public static async Task<ClaudeCliUpdateStatus> GetUpdateStatusAsync(string? cliPath = null, CancellationToken cancellationToken = default)
    {
        var resolvedPath = Transport.CliLocator.FindCli(cliPath);
        var installedVersion = await Internal.ClaudeCliMetadataReader.ReadInstalledVersionAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        var (latestVersion, errorMessage) = await Internal.ClaudeCliMetadataReader.ProbeLatestPublishedVersionAsync(cancellationToken).ConfigureAwait(false);

        if (errorMessage is not null)
        {
            return new ClaudeCliUpdateStatus(installedVersion, null, false, $"Failed to check latest Claude Code version from GitHub: {errorMessage}");
        }

        if (latestVersion is null)
        {
            return new ClaudeCliUpdateStatus(installedVersion, null, false, null);
        }

        if (!Internal.ClaudeCliMetadataReader.IsNewerVersion(latestVersion, installedVersion))
        {
            return new ClaudeCliUpdateStatus(installedVersion, latestVersion, false, null);
        }

        return new ClaudeCliUpdateStatus(
            installedVersion,
            latestVersion,
            true,
            $"Claude Code update is available: installed {installedVersion}, latest {latestVersion}. Run '{ClaudeCliUpdateStatus.UpdateCommand}'.");
    }
}
