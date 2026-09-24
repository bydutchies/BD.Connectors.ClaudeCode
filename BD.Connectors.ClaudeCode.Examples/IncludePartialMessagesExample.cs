using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example -- streams StreamEvent messages interleaved with regular
// messages as Claude generates its response.
internal static class IncludePartialMessagesExample
{
    public static async Task RunAsync()
    {
        Console.WriteLine("Partial Message Streaming Example");
        Console.WriteLine(new string('=', 50));

        var options = new ClaudeAgentOptions
        {
            IncludePartialMessages = true,
            MaxTurns = 2,
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();

        const string prompt = "Think of three jokes, then tell one";
        Console.WriteLine($"Prompt: {prompt}\n");
        Console.WriteLine(new string('=', 50));

        await client.QueryAsync(prompt);

        await foreach (var message in client.ReceiveResponseAsync())
        {
            Console.WriteLine(message);
        }
    }
}
