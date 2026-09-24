using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example. SettingSources controls where Claude Code loads
// configuration from: User (~/.claude/), Project (.claude/ in the project), Local
// (gitignored .claude-local/). Null (the default) loads the CLI's default sources; an empty list
// disables all filesystem setting sources.
internal static class SettingSourcesExample
{
    public static async Task RunAsync()
    {
        await RunAsync("=== Default Behavior Example ===", "SettingSources: null (default) -- CLI defaults (user, project, local) apply", null);
        await RunAsync("=== Disable All Sources Example ===", "SettingSources: [] -- no filesystem settings are loaded", []);
        await RunAsync("=== User Settings Only Example ===", "SettingSources: [User] -- project settings are excluded", [SettingSource.User]);
        await RunAsync(
            "=== Project + User Settings Example ===",
            "SettingSources: [User, Project]",
            [SettingSource.User, SettingSource.Project]);
    }

    private static async Task RunAsync(string heading, string description, IReadOnlyList<SettingSource>? settingSources)
    {
        Console.WriteLine(heading);
        Console.WriteLine(description);

        var options = new ClaudeAgentOptions { SettingSources = settingSources };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();
        await client.QueryAsync("What is 2 + 2?");

        await foreach (var message in client.ReceiveResponseAsync())
        {
            if (message is SystemMessage { Subtype: "init" } system)
            {
                var commands = system.Data["slash_commands"]?.AsArray().Count ?? 0;
                Console.WriteLine($"Available slash commands: {commands}");
                break;
            }
        }

        Console.WriteLine();
    }
}
