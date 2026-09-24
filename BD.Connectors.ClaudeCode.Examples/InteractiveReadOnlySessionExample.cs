using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Interactive, read-only Q&A session scoped to a fixed directory: connects a ClaudeSdkClient with
// only read-oriented tools available (Read/Glob/Grep -- no Write/Edit/Bash/NotebookEdit), asks what
// the application in that directory does, then keeps reading follow-up questions from the console
// until the user types "exit". Unlike this project's other examples, this one is interactive and is
// therefore not included in the "all" batch run (see Program.cs) -- run it by name instead.
internal static class InteractiveReadOnlySessionExample
{
    private const string WorkingDirectory = @"C:\Claude";

    public static async Task RunAsync()
    {
        Console.WriteLine("=== Interactive Read-Only Session Example ===");
        Console.WriteLine($"Directory: {WorkingDirectory} (read-only tools: Read, Glob, Grep)");
        Console.WriteLine("Type a follow-up question after each response, or \"exit\" to quit.\n");

        var options = new ClaudeAgentOptions
        {
            Cwd = WorkingDirectory,
            // Read-only guarantee: only read-oriented tools are made available to the model at all,
            // so there is no Write/Edit/Bash tool to grant or deny permission for in the first place.
            Tools = new ToolsConfig.Named(["Read", "Glob", "Grep"]),
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();

        await AskAsync(client, "What does the application in this directory do?");

        while (true)
        {
            Console.Write("\nUser: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input) || string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await AskAsync(client, input);
        }

        Console.WriteLine();
    }

    // Does not echo `prompt` itself: the CLI streams the just-sent turn back as its own UserMessage
    // before the assistant's reply, and DisplayHelper.Display already prints that -- an extra
    // Console.WriteLine here would print the same line twice.
    private static async Task AskAsync(ClaudeSdkClient client, string prompt)
    {
        await client.QueryAsync(prompt);
        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }
    }
}
