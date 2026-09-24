namespace BD.Connectors.ClaudeCode.Errors;

public class CliConnectionException(string message, Exception? innerException = null) : ClaudeSdkException(message, innerException)
{
}
