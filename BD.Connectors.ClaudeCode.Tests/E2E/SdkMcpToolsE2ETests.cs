using BD.Connectors.ClaudeCode.Mcp;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Tests.Shared;

namespace BD.Connectors.ClaudeCode.Tests.E2E;

// Real, authenticated CLI (model "haiku"). Ports the relevant scenario from PY's e2e tests: Claude
// calls a C# SDK MCP tool in an E2E test and the result comes back in the response. Focuses on tool
// execution mechanics (allowed_tools / disallowed_tools gating, multiple tools in one turn), not
// specific tool functionality -- same scope as the PY suite. Never run in CI
// (RequiresAuthenticatedClaude skips without a logged-in CLI); like QueryPermissionE2ETests, run
// TestKind=E2E only deliberately (see PORTING_STATUS.md Fase 3's note on an earlier session's
// accidental real CLI/model use).
[Property("TestKind", "E2E")]
[RequiresAuthenticatedClaude]
public class SdkMcpToolsE2ETests
{
    [Test]
    public async Task ToolExecution_AllowedSdkMcpTool_IsCalledAndResultComesBack()
    {
        var executions = new List<string>();
        var echo = EchoTool(executions);
        var server = SdkMcp.CreateServer("test", "1.0.0", [echo]);

        var options = new ClaudeAgentOptions
        {
            Model = "haiku",
            McpServers = new Dictionary<string, McpServerConfig> { ["test"] = server },
            AllowedTools = ["mcp__test__echo"],
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();
        await client.QueryAsync("Call the mcp__test__echo tool with any text");
        await Drain(client);

        await Assert.That(executions).Contains("echo");
    }

    [Test]
    public async Task PermissionEnforcement_DisallowedToolIsNeverCalled_AllowedToolIsCalled()
    {
        var executions = new List<string>();
        var echo = EchoTool(executions);
        var greet = GreetTool(executions);
        var server = SdkMcp.CreateServer("test", "1.0.0", [echo, greet]);

        var options = new ClaudeAgentOptions
        {
            Model = "haiku",
            McpServers = new Dictionary<string, McpServerConfig> { ["test"] = server },
            DisallowedTools = ["mcp__test__echo"],
            AllowedTools = ["mcp__test__greet"],
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();
        await client.QueryAsync(
            "First use the greet tool to greet 'Alice'. After that completes, use the echo tool to echo 'test'. "
            + "Do these one at a time, not in parallel.");
        await Drain(client);

        await Assert.That(executions).DoesNotContain("echo");
        await Assert.That(executions).Contains("greet");
    }

    [Test]
    public async Task MultipleTools_BothCalledInOneTurn()
    {
        var executions = new List<string>();
        var echo = EchoTool(executions);
        var greet = GreetTool(executions);
        var server = SdkMcp.CreateServer("multi", "1.0.0", [echo, greet]);

        var options = new ClaudeAgentOptions
        {
            Model = "haiku",
            McpServers = new Dictionary<string, McpServerConfig> { ["multi"] = server },
            AllowedTools = ["mcp__multi__echo", "mcp__multi__greet"],
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();
        await client.QueryAsync("Call mcp__multi__echo with text='test' and mcp__multi__greet with name='Bob'");
        await Drain(client);

        await Assert.That(executions).Contains("echo");
        await Assert.That(executions).Contains("greet");
    }

    [Test]
    public async Task WithoutAllowedTools_SdkMcpToolIsNeverCalled()
    {
        var executions = new List<string>();
        var echo = EchoTool(executions);
        var server = SdkMcp.CreateServer("noperm", "1.0.0", [echo]);

        var options = new ClaudeAgentOptions
        {
            Model = "haiku",
            McpServers = new Dictionary<string, McpServerConfig> { ["noperm"] = server },
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();
        await client.QueryAsync("Call the mcp__noperm__echo tool");
        await Drain(client);

        await Assert.That(executions).DoesNotContain("echo");
    }

    private static SdkMcpTool EchoTool(List<string> executions) =>
        SdkMcp.Tool("echo", "Echo back the input text", new Dictionary<string, Type> { ["text"] = typeof(string) }, (args, _) =>
        {
            executions.Add("echo");
            return Task.FromResult(new McpToolResult([new McpTextContent($"Echo: {args["text"]}")]));
        });

    private static SdkMcpTool GreetTool(List<string> executions) =>
        SdkMcp.Tool("greet", "Greet a person by name", new Dictionary<string, Type> { ["name"] = typeof(string) }, (args, _) =>
        {
            executions.Add("greet");
            return Task.FromResult(new McpToolResult([new McpTextContent($"Hello, {args["name"]}!")]));
        });

    private static async Task Drain(ClaudeSdkClient client)
    {
        await foreach (var _ in client.ReceiveResponseAsync())
        {
        }
    }
}
