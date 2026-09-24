namespace BD.Connectors.ClaudeCode.Messages;

// Not part of the PY port. Aggregate returned by ClaudeSdkClient.ReceiveFullResponseAsync, for
// callers who prefer a single object with readonly properties over iterating the message stream
// themselves. Mirrors what ReceiveResponseAsync yields (everything up to and including the
// ResultMessage), just collected into a list first.
public sealed record ClaudeResponse(IReadOnlyList<Message> Messages)
{
    public IReadOnlyList<UserMessage> UserMessages { get; } =
        Messages.OfType<UserMessage>().ToList();

    public IReadOnlyList<AssistantMessage> AssistantMessages { get; } =
        Messages.OfType<AssistantMessage>().ToList();

    public IReadOnlyList<SystemMessage> SystemMessages { get; } =
        Messages.OfType<SystemMessage>().ToList();

    // The stream is expected to always end with exactly one ResultMessage (that's what
    // ReceiveResponseAsync stops on); this throws only if the connection dropped mid-response.
    public ResultMessage Result { get; } =
        Messages.OfType<ResultMessage>().LastOrDefault()
        ?? throw new InvalidOperationException("No ResultMessage was received in the response stream.");

    // Every TextBlock across all AssistantMessages, concatenated in order -- the most common
    // "just give me the reply text" access pattern.
    public string Text { get; } = string.Concat(
        Messages.OfType<AssistantMessage>()
            .SelectMany(m => m.Content)
            .OfType<TextBlock>()
            .Select(t => t.Text));

    // All tool calls the assistant made while producing this response, in order.
    public IReadOnlyList<ToolUseBlock> ToolUses { get; } =
        Messages.OfType<AssistantMessage>()
            .SelectMany(m => m.Content)
            .OfType<ToolUseBlock>()
            .ToList();

    // The results of those tool calls, as sent back to the assistant. Tool results come back as
    // content blocks on UserMessages, not on the AssistantMessage that requested them.
    public IReadOnlyList<ToolResultBlock> ToolResults { get; } =
        Messages.OfType<UserMessage>()
            .SelectMany(m => m.BlockContent ?? [])
            .OfType<ToolResultBlock>()
            .ToList();

    public bool IsError => Result.IsError;

    public double? TotalCostUsd => Result.TotalCostUsd;

    // Use to resume/continue this conversation later (ClaudeAgentOptions.Resume /
    // ClaudeSdkClient.QueryAsync(prompt, sessionId: ...)).
    public string SessionId => Result.SessionId;

    public string? StopReason => Result.StopReason;

    public long DurationMs => Result.DurationMs;

    public long DurationApiMs => Result.DurationApiMs;

    public int NumTurns => Result.NumTurns;

    public IReadOnlyList<string>? Errors => Result.Errors;

    // Model that produced the last assistant turn; differs from a requested ClaudeAgentOptions.Model
    // when the CLI fell back to ClaudeAgentOptions.FallbackModel.
    public string? Model => AssistantMessages.Count > 0 ? AssistantMessages[^1].Model : null;
}
