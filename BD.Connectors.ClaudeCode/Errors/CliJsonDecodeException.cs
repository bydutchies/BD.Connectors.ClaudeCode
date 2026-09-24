namespace BD.Connectors.ClaudeCode.Errors;

public sealed class CliJsonDecodeException(string line, Exception originalError)
    : ClaudeSdkException(CliJsonDecodeException.BuildMessage(line))
{
    private const int MaxLinePreviewLength = 100;

    public string Line { get; } = line;

    public Exception OriginalError { get; } = originalError;

    private static string BuildMessage(string line)
    {
        var preview = line.Length > MaxLinePreviewLength ? line[..MaxLinePreviewLength] : line;
        return $"Failed to decode JSON: {preview}...";
    }
}
