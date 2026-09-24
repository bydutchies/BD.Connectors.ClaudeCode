using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example -- an in-process SDK MCP server with calculator tools.
internal static class McpCalculatorExample
{
    public static async Task RunAsync()
    {
        var calculator = SdkMcp.CreateServer(
            "calculator",
            "2.0.0",
            [
                SdkMcp.Tool("add", "Add two numbers", new Dictionary<string, Type> { ["a"] = typeof(double), ["b"] = typeof(double) }, AddAsync),
                SdkMcp.Tool("divide", "Divide one number by another", new Dictionary<string, Type> { ["a"] = typeof(double), ["b"] = typeof(double) }, DivideAsync),
                SdkMcp.Tool("sqrt", "Calculate square root", new Dictionary<string, Type> { ["n"] = typeof(double) }, SquareRootAsync),
            ]);

        var options = new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, McpServerConfig> { ["calc"] = calculator },
            AllowedTools = ["mcp__calc__add", "mcp__calc__divide", "mcp__calc__sqrt"],
        };

        string[] prompts = ["Calculate 15 + 27", "What is 100 divided by 7?", "Calculate the square root of 144"];

        foreach (var prompt in prompts)
        {
            Console.WriteLine(new string('=', 50));
            Console.WriteLine($"Prompt: {prompt}");
            Console.WriteLine(new string('=', 50));

            await using var client = new ClaudeSdkClient(options);
            await client.ConnectAsync();
            await client.QueryAsync(prompt);

            await foreach (var message in client.ReceiveResponseAsync())
            {
                DisplayHelper.Display(message);
            }
        }
    }

    private static Task<McpToolResult> AddAsync(System.Text.Json.Nodes.JsonObject args, CancellationToken cancellationToken)
    {
        var a = args["a"]!.GetValue<double>();
        var b = args["b"]!.GetValue<double>();
        return Task.FromResult(new McpToolResult([new McpTextContent($"{a} + {b} = {a + b}")]));
    }

    private static Task<McpToolResult> DivideAsync(System.Text.Json.Nodes.JsonObject args, CancellationToken cancellationToken)
    {
        var a = args["a"]!.GetValue<double>();
        var b = args["b"]!.GetValue<double>();
        if (b == 0)
        {
            return Task.FromResult(new McpToolResult([new McpTextContent("Error: Division by zero is not allowed")], IsError: true));
        }

        return Task.FromResult(new McpToolResult([new McpTextContent($"{a} / {b} = {a / b}")]));
    }

    private static Task<McpToolResult> SquareRootAsync(System.Text.Json.Nodes.JsonObject args, CancellationToken cancellationToken)
    {
        var n = args["n"]!.GetValue<double>();
        if (n < 0)
        {
            return Task.FromResult(new McpToolResult([new McpTextContent($"Error: Cannot calculate square root of negative number {n}")], IsError: true));
        }

        return Task.FromResult(new McpToolResult([new McpTextContent($"sqrt({n}) = {Math.Sqrt(n)}")]));
    }
}
