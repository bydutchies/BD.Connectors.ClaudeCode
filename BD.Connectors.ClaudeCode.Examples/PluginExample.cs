using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example. Loads a local plugin directory (type "local" is the only
// supported plugin type -- see CliCommandBuilder) and shows it in the system init message.
internal static class PluginExample
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Plugin Example ===\n");

        var pluginPath = Path.Combine(AppContext.BaseDirectory, "plugins", "demo-plugin");

        var options = new ClaudeAgentOptions
        {
            Plugins = [new SdkPluginConfig("local", pluginPath)],
            MaxTurns = 1,
        };

        Console.WriteLine($"Loading plugin from: {pluginPath}\n");

        await foreach (var message in ClaudeAgent.QueryAsync("Hello!", options))
        {
            if (message is SystemMessage { Subtype: "init" } system)
            {
                Console.WriteLine("System initialized!");
                var plugins = system.Data["plugins"]?.AsArray();
                if (plugins is { Count: > 0 })
                {
                    Console.WriteLine("Plugins loaded:");
                    foreach (var plugin in plugins)
                    {
                        Console.WriteLine($"  - {plugin?["name"]} (path: {plugin?["path"]})");
                    }
                }
                else
                {
                    Console.WriteLine($"Plugin path configured: {pluginPath}");
                }
            }
        }
    }
}
