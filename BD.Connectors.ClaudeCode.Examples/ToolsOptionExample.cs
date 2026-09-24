using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example.
internal static class ToolsOptionExample
{
    public static async Task RunAsync()
    {
        await RunAsync("=== Tools Array Example ===", "Tools = Named([Read, Glob, Grep])", new ToolsConfig.Named(["Read", "Glob", "Grep"]));
        await RunAsync("=== Tools Empty Array Example ===", "Tools = Named([]) -- disables all built-in tools", new ToolsConfig.Named([]));
        await RunAsync("=== Tools Preset Example ===", "Tools = ClaudeCodePreset (all default Claude Code tools)", new ToolsConfig.ClaudeCodePreset());
    }

    private static async Task RunAsync(string heading, string description, ToolsConfig tools)
    {
        Console.WriteLine(heading);
        Console.WriteLine(description);

        var options = new ClaudeAgentOptions { Tools = tools, MaxTurns = 1 };

        await foreach (var message in ClaudeAgent.QueryAsync("What tools do you have available? Just list them briefly.", options))
        {
            switch (message)
            {
                case SystemMessage { Subtype: "init" } system:
                    var toolCount = system.Data["tools"]?.AsArray().Count ?? 0;
                    Console.WriteLine($"Tools from system message: {toolCount} tool(s)");
                    break;
                case AssistantMessage assistant:
                    foreach (var block in assistant.Content)
                    {
                        if (block is TextBlock text)
                        {
                            Console.WriteLine($"Claude: {text.Text}");
                        }
                    }

                    break;
            }
        }

        Console.WriteLine();
    }
}
