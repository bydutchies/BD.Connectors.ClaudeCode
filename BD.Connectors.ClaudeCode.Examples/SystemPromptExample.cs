using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example.
internal static class SystemPromptExample
{
    public static async Task RunAsync()
    {
        await RunWithAsync("=== No System Prompt (Vanilla Claude) ===", options: null);
        await RunWithAsync(
            "=== String System Prompt ===",
            new ClaudeAgentOptions { SystemPrompt = new SystemPromptConfig.Text("You are a pirate assistant. Respond in pirate speak.") });
        await RunWithAsync(
            "=== Preset System Prompt (Default) ===",
            new ClaudeAgentOptions { SystemPrompt = new SystemPromptConfig.Preset() });
        await RunWithAsync(
            "=== Preset System Prompt with Append ===",
            new ClaudeAgentOptions { SystemPrompt = new SystemPromptConfig.Preset(Append: "Always end your response with a fun fact.") });
    }

    private static async Task RunWithAsync(string heading, ClaudeAgentOptions? options)
    {
        Console.WriteLine(heading);

        await foreach (var message in ClaudeAgent.QueryAsync("What is 2 + 2?", options))
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
}
