using BD.Connectors.ClaudeCode.Hooks;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example -- a curated subset (PreToolUse blocking + UserPromptSubmit
// context) of that example's five scenarios.
internal static class HooksExample
{
    public static async Task RunAsync()
    {
        await PreToolUseAsync();
        await UserPromptSubmitAsync();
    }

    // Demonstrates how PreToolUse can block some bash commands but not others.
    private static async Task PreToolUseAsync()
    {
        Console.WriteLine("=== PreToolUse Example ===");

        var options = new ClaudeAgentOptions
        {
            AllowedTools = ["Bash"],
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [new HookMatcher("Bash", [CheckBashCommandAsync])],
            },
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();

        Console.WriteLine("User: Run the bash command: ./foo.sh --help");
        await client.QueryAsync("Run the bash command: ./foo.sh --help");
        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine();
    }

    // Demonstrates how a UserPromptSubmit hook can add context to every turn.
    private static async Task UserPromptSubmitAsync()
    {
        Console.WriteLine("=== UserPromptSubmit Example ===");

        var options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.UserPromptSubmit] = [new HookMatcher(null, [AddCustomInstructionsAsync])],
            },
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();

        Console.WriteLine("User: What's my favorite color?");
        await client.QueryAsync("What's my favorite color?");
        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine();
    }

    private static Task<HookJsonOutput> CheckBashCommandAsync(HookInput input, string? toolUseId, HookContext context)
    {
        if (input is PreToolUseHookInput { ToolName: "Bash" } preToolUse
            && preToolUse.ToolInput["command"]?.ToString() is { } command
            && command.Contains("foo.sh", StringComparison.Ordinal))
        {
            Console.WriteLine($"   Blocked command: {command}");
            return Task.FromResult<HookJsonOutput>(new SyncHookJsonOutput
            {
                HookSpecificOutput = new PreToolUseHookSpecificOutput(
                    PermissionDecision: "deny",
                    PermissionDecisionReason: "Command contains invalid pattern: foo.sh"),
            });
        }

        return Task.FromResult<HookJsonOutput>(new SyncHookJsonOutput());
    }

    private static Task<HookJsonOutput> AddCustomInstructionsAsync(HookInput input, string? toolUseId, HookContext context) =>
        Task.FromResult<HookJsonOutput>(new SyncHookJsonOutput
        {
            HookSpecificOutput = new UserPromptSubmitHookSpecificOutput("My favorite color is hot pink"),
        });
}
