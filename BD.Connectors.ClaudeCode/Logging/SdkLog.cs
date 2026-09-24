using Microsoft.Extensions.Logging;

namespace BD.Connectors.ClaudeCode.Logging;

internal static partial class SdkLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Message}")]
    public static partial void CanUseToolShadowed(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Starting CLI transport: {CliPath}")]
    public static partial void TransportStarting(ILogger logger, string cliPath);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Skipping non-JSON line from CLI stdout: {Line}")]
    public static partial void SkippingNonJsonLine(ILogger logger, string line);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Claude Code version {Version} at {CliPath} is unsupported in the Agent SDK. Minimum required version is {MinimumVersion}. Some features may not work correctly.")]
    public static partial void CliVersionTooOld(ILogger logger, string version, string cliPath, string minimumVersion);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "verbatim_prompts is enabled, but Claude Code version {Version} at {CliPath} ignores it: prompts will still have @path mentions expanded and slash commands dispatched. Claude Code {MinimumVersion} or later is required.")]
    public static partial void VerbatimPromptsUnsupported(ILogger logger, string version, string cliPath, string minimumVersion);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "stderr callback raised; continuing")]
    public static partial void StderrCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Failed to kill process {ProcessId}")]
    public static partial void ProcessKillFailed(ILogger logger, int processId, Exception exception);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Failed to parse settings as JSON, treating as file path: {SettingsText}")]
    public static partial void SettingsParseFailedTreatingAsFilePath(ILogger logger, string settingsText);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Settings file not found: {SettingsPath}")]
    public static partial void SettingsFileNotFound(ILogger logger, string settingsPath);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Claude Code version check failed; continuing without it")]
    public static partial void VersionCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "OTEL trace context injection failed")]
    public static partial void OtelInjectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Failed to close stdin cleanly")]
    public static partial void StdinCloseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug, Message = "stderr stream read failed")]
    public static partial void StderrStreamReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "Dropping truncated JSON at end of CLI stdout: {Line}")]
    public static partial void DroppingTruncatedTail(ILogger logger, string line);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug, Message = "Process exit wait failed; treating exit code as unknown")]
    public static partial void ProcessExitWaitFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 16, Level = LogLevel.Error, Message = "Fatal error in message reader")]
    public static partial void FatalErrorInMessageReader(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "Replacing ProcessError (exit code {ExitCode}) with ResultError")]
    public static partial void ReplacingProcessErrorWithResultError(ILogger logger, int? exitCode);

    [LoggerMessage(EventId = 18, Level = LogLevel.Debug, Message = "Result received with {Count} task(s) in flight; keeping stdin open")]
    public static partial void ResultReceivedWithTasksInFlight(ILogger logger, int count);

    [LoggerMessage(EventId = 19, Level = LogLevel.Warning, Message = "Dropping mirror_error message (buffer full)")]
    public static partial void DroppingMirrorErrorMessage(ILogger logger);

    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Unhandled exception in ControlProtocol background task")]
    public static partial void UnhandledBackgroundTaskException(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Prompt stream failed; closing stdin")]
    public static partial void PromptStreamFailedClosingStdin(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 22, Level = LogLevel.Debug, Message = "Error closing input stream")]
    public static partial void ErrorClosingInputStream(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 23, Level = LogLevel.Debug, Message = "Failed to write control_response (transport closed)")]
    public static partial void ControlResponseWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 24,
        Level = LogLevel.Debug,
        Message = "Waiting for a run-ending result before closing stdin (sdkMcpBridges={SdkMcpBridgeCount}, hasHooks={HasHooks}, hasCanUseTool={HasCanUseTool})")]
    public static partial void WaitingForRunEndingResult(ILogger logger, int sdkMcpBridgeCount, bool hasHooks, bool hasCanUseTool);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Binary embedded resource cannot be converted to text, skipping")]
    public static partial void BinaryEmbeddedResourceSkipped(ILogger logger);

    [LoggerMessage(EventId = 26, Level = LogLevel.Debug, Message = "SDK MCP server {ServerName} failed while handling a message")]
    public static partial void McpServerFailed(ILogger logger, string serverName, Exception exception);

    [LoggerMessage(
        EventId = 27,
        Level = LogLevel.Warning,
        Message = "[SessionStore] dropping mirror frame: filePath {FilePath} is not under {ProjectsDir} -- subprocess CLAUDE_CONFIG_DIR likely differs from parent (custom env / container?)")]
    public static partial void MirrorAppendFrameNotUnderProjectsDir(ILogger logger, string filePath, string projectsDir);

    [LoggerMessage(
        EventId = 28,
        Level = LogLevel.Debug,
        Message = "[TranscriptMirrorBatcher] append timed out after {TimeoutSeconds:F1}s for {FilePath} -- not retrying")]
    public static partial void MirrorAppendTimedOut(ILogger logger, double timeoutSeconds, string filePath);

    [LoggerMessage(
        EventId = 29,
        Level = LogLevel.Debug,
        Message = "[TranscriptMirrorBatcher] append attempt {Attempt}/{MaxAttempts} failed for {FilePath}")]
    public static partial void MirrorAppendAttemptFailed(ILogger logger, int attempt, int maxAttempts, string filePath, Exception exception);

    [LoggerMessage(EventId = 30, Level = LogLevel.Error, Message = "[TranscriptMirrorBatcher] flush failed for {FilePath}")]
    public static partial void MirrorAppendFlushFailed(ILogger logger, string filePath, Exception exception);

    [LoggerMessage(EventId = 31, Level = LogLevel.Error, Message = "[TranscriptMirrorBatcher] on_error callback raised")]
    public static partial void MirrorOnErrorCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "[TranscriptMirrorBatcher] drain raised")]
    public static partial void MirrorDrainRaised(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 33, Level = LogLevel.Debug, Message = "[TranscriptMirrorBatcher] close flush failed")]
    public static partial void MirrorCloseFlushFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 34, Level = LogLevel.Warning, Message = "[SessionStore] resume: skipping {Path}")]
    public static partial void ResumeSkippingFile(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 35, Level = LogLevel.Warning, Message = "[SessionStore] skipping unsafe subpath from list_subkeys: {Subpath}")]
    public static partial void ResumeSkippingUnsafeSubpath(ILogger logger, string subpath);

    [LoggerMessage(EventId = 36, Level = LogLevel.Debug, Message = "list_session_summaries without list_sessions: gap-fill skipped; sessions lacking a sidecar will be omitted")]
    public static partial void SessionSummariesWithoutListSessions(ILogger logger);
}
