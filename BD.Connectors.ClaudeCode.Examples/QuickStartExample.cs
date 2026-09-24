using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example.
internal static class QuickStartExample
{
    public static async Task RunAsync()
    {
        await BasicAsync();
        await WithOptionsAsync();
        await WithToolsAsync();
    }

    private static async Task BasicAsync()
    {
        Console.WriteLine("=== Basic Example ===");

        await foreach (var message in ClaudeAgent.QueryAsync("What is 2 + 2?"))
        {
            if (message is AssistantMessage assistant)
            {
                foreach (var block in assistant.Content)
                {
                    if (block is TextBlock text)
                    {
                        Console.WriteLine($"Claude: {text.Text}");
                    }
                }
            }
        }

        Console.WriteLine();
    }

    private static async Task WithOptionsAsync()
    {
        Console.WriteLine("=== With Options Example ===");

        var options = new ClaudeAgentOptions
        {
            SystemPrompt = new SystemPromptConfig.Text("You are a helpful assistant that explains things simply."),
            MaxTurns = 1,
        };

        await foreach (var message in ClaudeAgent.QueryAsync("Explain what .NET is in one sentence.", options))
        {
            if (message is AssistantMessage assistant)
            {
                foreach (var block in assistant.Content)
                {
                    if (block is TextBlock text)
                    {
                        Console.WriteLine($"Claude: {text.Text}");
                    }
                }
            }
        }

        Console.WriteLine();
    }

    private static async Task WithToolsAsync()
    {
        Console.WriteLine("=== With Tools Example ===");

        var options = new ClaudeAgentOptions
        {
            AllowedTools = ["Read", "Write"],
            SystemPrompt = new SystemPromptConfig.Text("You are a helpful file assistant."),
        };

        await foreach (var message in ClaudeAgent.QueryAsync("Create a file called hello.txt with 'Hello, World!' in it", options))
        {
            switch (message)
            {
                case AssistantMessage assistant:
                    foreach (var block in assistant.Content)
                    {
                        if (block is TextBlock text)
                        {
                            Console.WriteLine($"Claude: {text.Text}");
                        }
                    }

                    break;
                case ResultMessage { TotalCostUsd: > 0 } result:
                    Console.WriteLine($"\nCost: ${result.TotalCostUsd:F4}");
                    break;
            }
        }

        Console.WriteLine();
    }
}
