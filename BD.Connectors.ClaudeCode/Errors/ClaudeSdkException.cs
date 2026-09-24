namespace BD.Connectors.ClaudeCode.Errors;

public class ClaudeSdkException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}
