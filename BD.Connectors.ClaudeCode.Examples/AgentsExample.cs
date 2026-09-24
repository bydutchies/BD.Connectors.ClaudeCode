using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example.
internal static class AgentsExample
{
    public static async Task RunAsync()
    {
        await CodeReviewerAsync();
        await MultipleAgentsAsync();
    }

    private static async Task CodeReviewerAsync()
    {
        Console.WriteLine("=== Code Reviewer Agent Example ===");

        var options = new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["code-reviewer"] = new AgentDefinition(
                    Description: "Reviews code for best practices and potential issues",
                    Prompt: "You are a code reviewer. Analyze code for bugs, performance issues, "
                        + "security vulnerabilities, and adherence to best practices. Provide constructive feedback.")
                {
                    Tools = ["Read", "Grep"],
                    Model = "sonnet",
                },
            },
        };

        await foreach (var message in ClaudeAgent.QueryAsync(
            "Use the code-reviewer agent to review the code in Program.cs", options))
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine();
    }

    private static async Task MultipleAgentsAsync()
    {
        Console.WriteLine("=== Multiple Agents Example ===");

        var options = new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["analyzer"] = new AgentDefinition(
                    Description: "Analyzes code structure and patterns",
                    Prompt: "You are a code analyzer. Examine code structure, patterns, and architecture.")
                {
                    Tools = ["Read", "Grep", "Glob"],
                },
                ["tester"] = new AgentDefinition(
                    Description: "Creates and runs tests",
                    Prompt: "You are a testing expert. Write comprehensive tests and ensure code quality.")
                {
                    Tools = ["Read", "Write", "Bash"],
                    Model = "sonnet",
                },
            },
            SettingSources = [SettingSource.User, SettingSource.Project],
        };

        await foreach (var message in ClaudeAgent.QueryAsync(
            "Use the analyzer agent to find all C# files in this directory", options))
        {
            if (message is AssistantMessage or ResultMessage)
            {
                DisplayHelper.Display(message);
            }
        }

        Console.WriteLine();
    }
}
