namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example -- a curated subset of that example's ten scenarios
// (basic streaming, multi-turn, and interrupt) rather than a literal one-for-one port of every
// sub-example. Prompts are not echoed with a manual Console.WriteLine: the CLI streams the just-sent
// turn back as its own UserMessage before the assistant's reply, and DisplayHelper.Display already
// prints that -- echoing it again here would print the same line twice.
internal static class StreamingModeExample
{
    public static async Task RunAsync()
    {
        await BasicStreamingAsync();
        await MultiTurnConversationAsync();
        await WithInterruptAsync();
    }

    private static async Task BasicStreamingAsync()
    {
        Console.WriteLine("=== Basic Streaming Example ===");

        await using var client = new ClaudeSdkClient();
        await client.ConnectAsync();

        await client.QueryAsync("What is 2+2?");

        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine();
    }

    private static async Task MultiTurnConversationAsync()
    {
        Console.WriteLine("=== Multi-Turn Conversation Example ===");

        await using var client = new ClaudeSdkClient();
        await client.ConnectAsync();

        await client.QueryAsync("What's the capital of France?");
        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine();
        await client.QueryAsync("What's the population of that city?");
        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine();
    }

    private static async Task WithInterruptAsync()
    {
        Console.WriteLine("=== Interrupt Example ===");
        Console.WriteLine("IMPORTANT: Interrupts require active message consumption.");

        await using var client = new ClaudeSdkClient();
        await client.ConnectAsync();

        Console.WriteLine();
        await client.QueryAsync("Count from 1 to 100 slowly, with a brief pause between each number");

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var message in client.ReceiveResponseAsync())
            {
                DisplayHelper.Display(message);
            }
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        Console.WriteLine("\n[After 2 seconds, sending interrupt...]");
        await client.InterruptAsync();
        await consumeTask;

        Console.WriteLine();
        await client.QueryAsync("Never mind, just tell me a quick joke");
        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }

        Console.WriteLine();
    }
}
