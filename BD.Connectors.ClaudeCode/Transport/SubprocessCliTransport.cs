using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Logging;
using BD.Connectors.ClaudeCode.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BD.Connectors.ClaudeCode.Transport;

// Subprocess transport using the Claude Code CLI, ported from PY.
// Always uses streaming mode (--input-format stream-json, no --print): the prompt is written as a
// JSON message over stdin after connecting, which lets the control protocol send agents and other
// large configs via the initialize request.
public sealed partial class SubprocessCliTransport : ITransport, IDisposable
{
    private const int DefaultMaxBufferSize = 1024 * 1024;

    private readonly ClaudeAgentOptions _options;
    private readonly ILogger _logger;
    private readonly int _maxBufferSize;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string? _cwd;

    private string? _cliPath;
    private Process? _process;
    private Stream? _stdout;
    private Stream? _stdin;
    private Stream? _stderr;
    private CancellationTokenSource? _stderrCts;
    private Task? _stderrTask;
    private Exception? _exitError;

    public SubprocessCliTransport(ClaudeAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = options.Logger ?? NullLogger.Instance;
        _cliPath = options.CliPath;
        _cwd = options.Cwd;
        _maxBufferSize = options.MaxBufferSize ?? DefaultMaxBufferSize;
    }

    public bool IsReady { get; private set; }

    [GeneratedRegex(@"^([0-9]+\.[0-9]+\.[0-9]+)")]
    private static partial Regex VersionRegex();

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            return;
        }

        _cliPath ??= CliLocator.FindCli(_options.CliPath);

        // Guards the version probe below as well as the main spawn.
        CliLocator.RejectWindowsBatchCli(_cliPath, OperatingSystem.IsWindows());

        if (Environment.GetEnvironmentVariable(WireConstants.EnvVars.ClaudeAgentSdkSkipVersionCheck) is null)
        {
            await CheckClaudeVersionAsync().ConfigureAwait(false);
        }

        var args = CliCommandBuilder.Build(_cliPath, _options, OperatingSystem.IsWindows());

        // Checked before Process.Start: a bad working directory produces an unclear Win32Exception
        // from .NET, unlike Python's clean FileNotFoundError distinction.
        if (_cwd is not null && !Directory.Exists(_cwd))
        {
            var cwdError = new CliConnectionException($"Working directory does not exist: {_cwd}");
            _exitError = cwdError;
            throw cwdError;
        }

        var startInfo = new ProcessStartInfo(_cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = _options.Stderr is not null,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WorkingDirectory = _cwd ?? string.Empty,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ConfigureEnvironment(startInfo);

        SdkLog.TransportStarting(_logger, _cliPath);

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Win32Exception e)
        {
            var notFoundError = new CliNotFoundException($"Claude Code not found at: {_cliPath}", innerException: e);
            _exitError = notFoundError;
            throw notFoundError;
        }
        catch (Exception e)
        {
            var connectionError = new CliConnectionException($"Failed to start Claude Code: {e.Message}", e);
            _exitError = connectionError;
            throw connectionError;
        }

        if (_process is null)
        {
            var startError = new CliConnectionException("Failed to start Claude Code: process did not start");
            _exitError = startError;
            throw startError;
        }

        ProcessRegistry.Register(_process);

        _stdout = _process.StandardOutput.BaseStream;
        _stdin = _process.StandardInput.BaseStream;

        if (_options.Stderr is not null)
        {
            _stderr = _process.StandardError.BaseStream;
            _stderrCts = new CancellationTokenSource();
            _stderrTask = Task.Run(() => HandleStderrAsync(_stderrCts.Token), CancellationToken.None);
        }

        IsReady = true;
    }

    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // All checks inside the lock to prevent TOCTOU races with CloseAsync()/EndInputAsync().
            if (!IsReady || _stdin is null)
            {
                throw new CliConnectionException("ProcessTransport is not ready for writing");
            }

            if (_process is { HasExited: true })
            {
                throw new CliConnectionException($"Cannot write to terminated process (exit code: {_process.ExitCode})");
            }

            if (_exitError is not null)
            {
                throw new CliConnectionException($"Cannot write to process that exited with error: {_exitError.Message}", _exitError);
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                await _stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                IsReady = false;
                var writeError = new CliConnectionException($"Failed to write to process stdin: {e.Message}", e);
                _exitError = writeError;
                throw writeError;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stdin is not null)
            {
                try
                {
                    _stdin.Close();
                }
                catch (Exception e)
                {
                    SdkLog.StdinCloseFailed(_logger, e);
                }

                _stdin = null;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<JsonObject> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_process is null || _stdout is null)
        {
            throw new CliConnectionException("Not connected");
        }

        var framer = new LineFramer();
        var buffer = new byte[8192];

        while (true)
        {
            int read;
            try
            {
                read = await _stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            foreach (var lineBytes in framer.Push(buffer, read))
            {
                GuardBufferSize(lineBytes.Length);
                var data = ParseStdoutLine(Encoding.UTF8.GetString(lineBytes));
                if (data is not null)
                {
                    yield return data;
                }
            }

            GuardBufferSize(framer.PendingLength);
        }

        // The CLI terminates every message with "\n", so a residual tail means either a producer
        // that omits the final newline (yield it) or one cut off mid-write (unrecoverable, drop it).
        var tailBytes = framer.Flush();
        JsonObject? tailData = null;
        if (tailBytes.Length > 0)
        {
            var tailText = Encoding.UTF8.GetString(tailBytes);
            try
            {
                tailData = ParseStdoutLine(tailText);
            }
            catch (CliJsonDecodeException)
            {
                SdkLog.DroppingTruncatedTail(_logger, tailText.Length > 200 ? tailText[..200] : tailText);
            }
        }

        if (tailData is not null)
        {
            yield return tailData;
        }

        int returnCode;
        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            returnCode = _process.ExitCode;
        }
        catch (Exception e)
        {
            SdkLog.ProcessExitWaitFailed(_logger, e);
            returnCode = -1;
        }

        if (returnCode != 0)
        {
            var processError = new ProcessException($"Command failed with exit code {returnCode}", returnCode, "Check stderr output for details");
            _exitError = processError;
            throw processError;
        }
    }

    public async Task CloseAsync()
    {
        if (_process is null)
        {
            IsReady = false;
            return;
        }

        if (_stderrCts is not null)
        {
            await _stderrCts.CancelAsync().ConfigureAwait(false);
            if (_stderrTask is not null)
            {
                try
                {
                    await _stderrTask.ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    SdkLog.StderrStreamReadFailed(_logger, e);
                }
            }

            _stderrCts.Dispose();
        }

        _stderrCts = null;
        _stderrTask = null;

        var lockHeld = false;
        try
        {
            using var lockCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _writeLock.WaitAsync(lockCts.Token).ConfigureAwait(false);
            lockHeld = true;
        }
        catch (OperationCanceledException)
        {
            // A writer blocked on a full stdin pipe must not pin close() forever; proceed without
            // the lock in that case.
        }

        try
        {
            IsReady = false; // set inside the lock (when held) to prevent a TOCTOU race with WriteAsync()
            if (_stdin is not null)
            {
                try
                {
                    _stdin.Close();
                }
                catch (Exception e)
                {
                    SdkLog.StdinCloseFailed(_logger, e);
                }

                _stdin = null;
            }
        }
        finally
        {
            if (lockHeld)
            {
                _writeLock.Release();
            }
        }

        if (_stderr is not null)
        {
            try
            {
                _stderr.Close();
            }
            catch (Exception e)
            {
                SdkLog.StdinCloseFailed(_logger, e);
            }

            _stderr = null;
        }

        // The subprocess needs time to flush its session file after receiving EOF on stdin; without
        // this grace period a SIGTERM can interrupt the write and lose the last assistant message.
        try
        {
            if (!_process.HasExited)
            {
                var exited = await WaitForExitWithTimeoutAsync(_process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (!exited && !OperatingSystem.IsWindows())
                {
                    TrySendSigterm(_process);
                    exited = await WaitForExitWithTimeoutAsync(_process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }

                if (!exited)
                {
                    TryKill(_process);
                    await WaitForExitWithTimeoutAsync(_process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Only stop tracking a child that was actually reaped; a still-running process (kill
            // raced, or wait timed out) stays registered so the process-exit reaper gets a chance.
            if (_process.HasExited)
            {
                ProcessRegistry.Unregister(_process);
            }
        }

        _process = null;
        _stdout = null;
        _stdin = null;
        _stderr = null;
        _exitError = null;
    }

    // Disposes what CloseAsync() did not already dispose (CloseAsync is expected to be called for a
    // connected transport; this is a backstop for a transport that was never connected, or whose
    // CloseAsync was skipped).
    public void Dispose()
    {
        _writeLock.Dispose();
        _stderrCts?.Dispose();
    }

    private static async Task<bool> WaitForExitWithTimeoutAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    private void TrySendSigterm(Process process)
    {
        try
        {
            _ = kill(process.Id, 15);
        }
        catch (Exception e)
        {
            SdkLog.ProcessKillFailed(_logger, process.Id, e);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e)
        {
            SdkLog.ProcessKillFailed(_logger, process.Id, e);
        }
    }

    private async Task HandleStderrAsync(CancellationToken cancellationToken)
    {
        if (_stderr is null)
        {
            return;
        }

        var framer = new LineFramer();
        var buffer = new byte[8192];

        void Emit(byte[] lineBytes)
        {
            var line = Encoding.UTF8.GetString(lineBytes).TrimEnd();
            if (line.Length == 0 || _options.Stderr is null)
            {
                return;
            }

            try
            {
                _options.Stderr(line);
            }
            catch (Exception e)
            {
                SdkLog.StderrCallbackFailed(_logger, e);
            }
        }

        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await _stderr.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                foreach (var lineBytes in framer.Push(buffer, read))
                {
                    Emit(lineBytes);
                }

                // A producer that never emits a newline can't grow the buffer without bound; flush it
                // as a partial line instead.
                if (framer.PendingLength > _maxBufferSize)
                {
                    Emit(framer.Flush());
                }
            }
        }
        catch (Exception e)
        {
            SdkLog.StderrStreamReadFailed(_logger, e);
        }
        finally
        {
            // Runs even when cancellation unwinds this task (CloseAsync cancels _stderrCts): the last
            // partial line, possibly a diagnostic written without a trailing newline right before the
            // CLI stalled, still reaches the callback.
            Emit(framer.Flush());
        }
    }

    private async Task CheckClaudeVersionAsync()
    {
        Process? versionProcess = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var startInfo = new ProcessStartInfo(_cliPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-v");

            versionProcess = Process.Start(startInfo);
            if (versionProcess is null)
            {
                return;
            }

            var output = await versionProcess.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            var match = VersionRegex().Match(output.Trim());
            if (!match.Success)
            {
                return;
            }

            var version = match.Groups[1].Value;
            var versionParts = ParseIntParts(version);

            if (CompareIntParts(versionParts, ParseIntParts(SdkInfo.MinimumCliVersion)) < 0)
            {
                SdkLog.CliVersionTooOld(_logger, version, _cliPath!, SdkInfo.MinimumCliVersion);
            }

            if (_options.VerbatimPrompts
                && CompareIntParts(versionParts, ParseIntParts(SdkInfo.VerbatimPromptsMinimumCliVersion)) < 0)
            {
                SdkLog.VerbatimPromptsUnsupported(_logger, version, _cliPath!, SdkInfo.VerbatimPromptsMinimumCliVersion);
            }
        }
        catch (Exception e)
        {
            SdkLog.VersionCheckFailed(_logger, e);
        }
        finally
        {
            if (versionProcess is not null)
            {
                try
                {
                    if (!versionProcess.HasExited)
                    {
                        versionProcess.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception e)
                {
                    SdkLog.ProcessKillFailed(_logger, versionProcess.Id, e);
                }

                try
                {
                    await versionProcess.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    SdkLog.ProcessExitWaitFailed(_logger, e);
                }
            }
        }
    }

    private static int[] ParseIntParts(string version) =>
        [.. version.Split('.').Select(part => int.Parse(part, CultureInfo.InvariantCulture))];

    private static int CompareIntParts(int[] left, int[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            var cmp = l.CompareTo(r);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    private void ConfigureEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Remove(WireConstants.EnvVars.ClaudeCode);
        startInfo.Environment[WireConstants.EnvVars.ClaudeCodeEntrypoint] = "sdk-dotnet"; // beslissing B2

        foreach (var (key, value) in _options.Env)
        {
            startInfo.Environment[key] = value;
        }

        startInfo.Environment[WireConstants.EnvVars.ClaudeAgentSdkVersion] = SdkInfo.Version;

        ApplyOtelPropagation(startInfo.Environment);

        if (_options.EnableFileCheckpointing)
        {
            startInfo.Environment[WireConstants.EnvVars.ClaudeCodeEnableSdkFileCheckpointing] = "true";
        }

        if (_cwd is not null)
        {
            startInfo.Environment[WireConstants.EnvVars.Pwd] = _cwd;
        }
    }

    // Propagates the active OTEL trace context to the CLI so its spans parent under the caller's
    // distributed trace. A no-op if there is no active Activity. Never allowed to fail connect().
    private void ApplyOtelPropagation(IDictionary<string, string?> environment)
    {
        try
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return;
            }

            var carrier = new Dictionary<string, string>(StringComparer.Ordinal);
            DistributedContextPropagator.Current.Inject(activity, carrier, static (c, key, value) => ((Dictionary<string, string>)c!)[key] = value);

            if (!carrier.ContainsKey("traceparent"))
            {
                return;
            }

            // Active span present: scrub stale inherited W3C context before writing the fresh values,
            // so an inherited TRACESTATE is not paired with a new TRACEPARENT. Explicit
            // ClaudeAgentOptions.Env always wins.
            foreach (var key in new[] { WireConstants.EnvVars.Traceparent, WireConstants.EnvVars.Tracestate })
            {
                if (!_options.Env.ContainsKey(key))
                {
                    environment.Remove(key);
                }
            }

            foreach (var (rawKey, value) in carrier)
            {
                var key = rawKey.ToUpperInvariant();
                if (!_options.Env.ContainsKey(key))
                {
                    environment[key] = value;
                }
            }
        }
        catch (Exception e)
        {
            SdkLog.OtelInjectionFailed(_logger, e);
        }
    }

    // Parses one complete line of the CLI's NDJSON stdout. Returns null for lines that carry no
    // message: blank lines, and non-JSON output such as "[SandboxDebug] ..." that some CLI builds
    // write to stdout. A line that looks like JSON but does not parse is corrupt -- with proper line
    // framing there is no later data that could complete it -- so it throws rather than silently
    // dropping a message.
    private JsonObject? ParseStdoutLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        if (!line.StartsWith('{'))
        {
            SdkLog.SkippingNonJsonLine(_logger, line.Length > 200 ? line[..200] : line);
            return null;
        }

        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new CliJsonDecodeException(line, e);
        }
    }

    private void GuardBufferSize(int length)
    {
        if (length <= _maxBufferSize)
        {
            return;
        }

        throw new CliJsonDecodeException(
            $"JSON message exceeded maximum buffer size of {_maxBufferSize} bytes",
            new InvalidOperationException($"Buffer size {length} exceeds limit {_maxBufferSize}"));
    }
}
