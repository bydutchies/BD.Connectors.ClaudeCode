namespace BD.Connectors.ClaudeCode.Examples;

// Console entry point for all examples (ported from PY's examples/*.py, one file per topic).
// Requires a native `claude` CLI (>= SdkInfo.RecommendedCliVersion) on PATH, already logged in;
// running any example other than "list" makes real model calls.
//
// Usage:
//   dotnet run --project examples/BD.Connectors.ClaudeCode.Examples -- <example-name>
//   dotnet run --project examples/BD.Connectors.ClaudeCode.Examples -- all
internal static class Program
{
    private static readonly Dictionary<string, Func<Task>> _examples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["quick-start"] = QuickStartExample.RunAsync,
        ["streaming-mode"] = StreamingModeExample.RunAsync,
        ["tool-permission-callback"] = ToolPermissionCallbackExample.RunAsync,
        ["hooks"] = HooksExample.RunAsync,
        ["mcp-calculator"] = McpCalculatorExample.RunAsync,
        ["agents"] = AgentsExample.RunAsync,
        ["system-prompt"] = SystemPromptExample.RunAsync,
        ["max-budget-usd"] = MaxBudgetUsdExample.RunAsync,
        ["include-partial-messages"] = IncludePartialMessagesExample.RunAsync,
        ["stderr-callback"] = StderrCallbackExample.RunAsync,
        ["setting-sources"] = SettingSourcesExample.RunAsync,
        ["tools-option"] = ToolsOptionExample.RunAsync,
        ["plugin-example"] = PluginExample.RunAsync,
    };

    // Reads from Console.In and waits on the user indefinitely -- would hang the "all" batch run, so
    // these are only ever dispatched by explicit name, never included in it.
    private static readonly Dictionary<string, Func<Task>> _interactiveExamples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["interactive-readonly-session"] = InteractiveReadOnlySessionExample.RunAsync,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var name = args[0];
        if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (exampleName, run) in _examples)
            {
                Console.WriteLine($"### {exampleName} ###");
                await run().ConfigureAwait(false);
                Console.WriteLine(new string('-', 50) + "\n");
            }

            return 0;
        }

        if (_examples.TryGetValue(name, out var example) || _interactiveExamples.TryGetValue(name, out example))
        {
            await example().ConfigureAwait(false);
            return 0;
        }

        Console.WriteLine($"Error: Unknown example '{name}'");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -- <example-name>");
        Console.WriteLine();
        Console.WriteLine("Available examples:");
        Console.WriteLine("  all - Run all examples");
        foreach (var name in _examples.Keys)
        {
            Console.WriteLine($"  {name}");
        }

        Console.WriteLine();
        Console.WriteLine("Interactive examples (not included in \"all\"):");
        foreach (var name in _interactiveExamples.Keys)
        {
            Console.WriteLine($"  {name}");
        }
    }
}
