namespace BD.Connectors.ClaudeCode.Errors;

public class ProcessException(string message, int? exitCode = null, string? stderr = null, Exception? innerException = null)
    : ClaudeSdkException(ProcessException.BuildMessage(message, exitCode, stderr), innerException)
{
    public int? ExitCode { get; } = exitCode;

    public string? Stderr { get; } = stderr;

    private static string BuildMessage(string message, int? exitCode, string? stderr)
    {
        if (exitCode is not null)
        {
            message = $"{message} (exit code: {exitCode})";
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            message = $"{message}\nError output: {stderr}";
        }

        return message;
    }
}
