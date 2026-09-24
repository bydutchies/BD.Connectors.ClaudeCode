using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example.
internal static class MaxBudgetUsdExample
{
    public static async Task RunAsync()
    {
        Console.WriteLine("This example demonstrates using MaxBudgetUsd to control API costs.\n");

        await RunQueryAsync("=== Without Budget Limit ===", null, "What is 2 + 2?");
        await RunQueryAsync(
            "=== With Reasonable Budget ($0.10) ===",
            new ClaudeAgentOptions { MaxBudgetUsd = 0.10 },
            "What is 2 + 2?");
        await RunQueryAsync(
            "=== With Tight Budget ($0.0001) ===",
            new ClaudeAgentOptions { MaxBudgetUsd = 0.0001 },
            "Read the README.md file and summarize it");

        Console.WriteLine(
            "\nNote: Budget checking happens after each API call completes, so the final cost "
            + "may slightly exceed the specified budget.\n");
    }

    private static async Task RunQueryAsync(string heading, ClaudeAgentOptions? options, string prompt)
    {
        Console.WriteLine(heading);

        await foreach (var message in ClaudeAgent.QueryAsync(prompt, options))
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
                case ResultMessage result:
                    if (result.TotalCostUsd is { } cost)
                    {
                        Console.WriteLine($"Total cost: ${cost:F4}");
                    }

                    Console.WriteLine($"Status: {result.Subtype}");
                    if (result.Subtype == "error_max_budget_usd")
                    {
                        Console.WriteLine("Budget limit exceeded!");
                    }

                    break;
            }
        }

        Console.WriteLine();
    }
}
