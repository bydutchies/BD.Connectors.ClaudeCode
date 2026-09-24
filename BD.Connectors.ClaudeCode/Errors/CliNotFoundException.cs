namespace BD.Connectors.ClaudeCode.Errors;

public sealed class CliNotFoundException(string message = CliNotFoundException.DefaultMessage, string? cliPath = null, Exception? innerException = null)
    : CliConnectionException(CliNotFoundException.BuildMessage(message, cliPath), innerException)
{
    private const string DefaultMessage = "Claude Code not found";

    private static string BuildMessage(string message, string? cliPath)
    {
        return string.IsNullOrEmpty(cliPath) ? message : $"{message}: {cliPath}";
    }
}
