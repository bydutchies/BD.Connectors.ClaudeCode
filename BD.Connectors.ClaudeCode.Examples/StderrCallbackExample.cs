using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example.
internal static class StderrCallbackExample
{
    public static async Task RunAsync()
    {
        var stderrLines = new List<string>();

        // The callback receives any stderr the CLI emits (warnings, errors). For verbose CLI debug
        // logs, pass ExtraArgs["debug-file"] = "/path/to/log" and read that file instead.
        var options = new ClaudeAgentOptions
        {
            Stderr = line =>
            {
                stderrLines.Add(line);
                if (line.Contains("[ERROR]", StringComparison.Ordinal))
                {
                    Console.WriteLine($"Error detected: {line}");
                }
            },
        };

        Console.WriteLine("Running query with stderr capture...");
        await foreach (var message in ClaudeAgent.QueryAsync("What is 2+2?", options))
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine($"\nCaptured {stderrLines.Count} stderr lines");
        if (stderrLines.Count > 0)
        {
            Console.WriteLine($"First stderr line: {stderrLines[0][..Math.Min(100, stderrLines[0].Length)]}");
        }
    }
}
