namespace BD.Connectors.ClaudeCode.Messages;

public static class TaskStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
    public const string Killed = "killed";

    public static readonly IReadOnlyCollection<string> Terminal = [Completed, Failed, Stopped, Killed];
}
