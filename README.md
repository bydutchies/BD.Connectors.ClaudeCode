# BD.Connectors.ClaudeCode

[![NuGet](https://img.shields.io/nuget/v/BD.Connectors.ClaudeCode.svg)](https://www.nuget.org/packages/BD.Connectors.ClaudeCode)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A .NET port of [`claude-agent-sdk-python`](https://github.com/anthropics/claude-agent-sdk-python)
(version 0.2.159) for driving the `claude` CLI from C#: one-shot/streaming queries, the full interactive
`ClaudeSdkClient` (permission prompts, hooks, interrupts, dynamic mode/model switches), in-process
SDK MCP servers, and local/session-store-backed session management.

> **Unofficial, independent project.** Not published, maintained or endorsed by Anthropic. "Claude"
> and "Anthropic" are trademarks of Anthropic, PBC; this package only automates the publicly
> installable `claude` CLI and is not affiliated with its maker.

This is a behavioral port, not a wrapper around the Python package — it talks to the `claude`
executable directly over its `stream-json` control protocol.

## Requirements

- .NET 10.
- A native `claude` CLI installed and on `PATH` (or pointed to via `ClaudeAgentOptions.CliPath`),
  version **2.1.281 or newer** recommended (`2.0.0` minimum; `2.1.248+` if you use
  `VerbatimPrompts`). Windows `.cmd`/`.bat` shims are rejected outright (CVE-2024-27980) — install
  the native binary via `irm https://claude.ai/install.ps1 | iex` (Windows) or
  `curl -fsSL https://claude.ai/install.sh | bash` (macOS/Linux).
- An authenticated CLI session (`claude` already logged in) for anything beyond basic connectivity
  checks — this library never reads `ANTHROPIC_API_KEY` itself, exactly like the CLI it drives.
- Use of the `claude` CLI this library drives is subject to
  [Anthropic's own terms of service](https://www.anthropic.com/legal/consumer-terms), independent of
  this package's MIT license — this library only automates that CLI, it does not embed or
  redistribute it.

## Install

```bash
dotnet add package BD.Connectors.ClaudeCode
```

## Quick start

```csharp
using BD.Connectors.ClaudeCode;

await foreach (var message in ClaudeAgent.QueryAsync("What is 2 + 2?"))
{
    if (message is AssistantMessage assistant)
    {
        foreach (var block in assistant.Content)
        {
            if (block is TextBlock text)
                Console.WriteLine(text.Text);
        }
    }
}
```

`ClaudeAgent.QueryAsync` is one-shot (or unidirectional streaming, if you pass an
`IAsyncEnumerable<JsonObject>` prompt): no conversation state, no interrupt support. For anything
interactive, use `ClaudeSdkClient`.

## Interactive client

```csharp
using BD.Connectors.ClaudeCode;

await using var client = new ClaudeSdkClient(new ClaudeAgentOptions
{
    Model = "claude-opus-4-5",
    Cwd = "/path/to/project",
});

await client.ConnectAsync();
await client.QueryAsync("List the files in this directory.");

await foreach (var message in client.ReceiveResponseAsync())
{
    // ReceiveResponseAsync completes after this turn's ResultMessage;
    // ReceiveMessagesAsync instead streams for the client's whole lifetime.
}

await client.QueryAsync("Now summarize what you found.");
await foreach (var message in client.ReceiveResponseAsync())
{
    // ...
}

await client.InterruptAsync();
await client.SetPermissionModeAsync(PermissionMode.AcceptEdits);
await client.SetModelAsync("claude-sonnet-5");
```

`ClaudeSdkClient` also exposes `RewindFilesAsync`, `ReconnectMcpServerAsync`,
`ToggleMcpServerAsync`, `StopTaskAsync`, `GetMcpStatusAsync`, `GetContextUsageAsync` and
`GetServerInfo()` for the rest of the CLI's dynamic control protocol.

## Permission callback (`can_use_tool`)

```csharp
var options = new ClaudeAgentOptions
{
    CanUseTool = async (toolName, input, context) =>
    {
        if (toolName == "Bash" && input["command"]?.ToString().Contains("rm -rf") == true)
            return new PermissionResultDeny("Refusing destructive command.");

        return new PermissionResultAllow();
    },
};
```

`PermissionResultAllow` can also carry `UpdatedInput`/`UpdatedPermissions`; `PermissionResultDeny`
can set `Interrupt = true` to abort the turn. Setting `CanUseTool` automatically routes the CLI's
tool-permission prompts through this callback (`--permission-prompt-tool stdio`), so it works
headlessly — no interactive terminal required.

## Hooks

```csharp
var options = new ClaudeAgentOptions
{
    Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
    {
        [HookEvent.PreToolUse] = new[]
        {
            new HookMatcher("Bash", new HookCallback[]
            {
                async (input, toolUseId, context) => new HookJsonOutput { Continue = true },
            }),
        },
    },
};
```

## In-process MCP tools

```csharp
using BD.Connectors.ClaudeCode.Mcp;

var addTool = SdkMcp.Tool(
    "add",
    "Add two numbers",
    new Dictionary<string, Type> { ["a"] = typeof(double), ["b"] = typeof(double) },
    async (input, ct) => new McpToolResult(new[]
    {
        new McpTextContent((input["a"]!.GetValue<double>() + input["b"]!.GetValue<double>()).ToString()),
    }));

var calculator = SdkMcp.CreateServer("calculator", tools: new[] { addTool });

var options = new ClaudeAgentOptions
{
    McpServers = new Dictionary<string, McpServerConfig> { ["calc"] = calculator },
};
```

`SdkMcp.Tool` also has an overload taking a raw JSON Schema `JsonObject`, and a generic
`Tool<TArgs>(name, description, JsonTypeInfo<TArgs>, handler)` overload that derives the schema from
a source-generated type (AOT/trimming-safe — prefer this one in trimmed/NativeAOT apps).
`McpServerConfig` also has `McpStdioServerConfig`, `McpSseServerConfig` and `McpHttpServerConfig`
subtypes for out-of-process MCP servers.

## Sessions and resume

```csharp
using BD.Connectors.ClaudeCode.Sessions;

var sessions = await ClaudeSessions.ListSessionsAsync();
var messages = await ClaudeSessions.GetSessionMessagesAsync(sessions[0].SessionId);
await ClaudeSessions.RenameSessionAsync(sessions[0].SessionId, "My renamed session");
var fork = await ClaudeSessions.ForkSessionAsync(sessions[0].SessionId);
```

Every local session method above (`ListSessionsAsync`, `GetSessionInfoAsync`,
`GetSessionMessagesAsync`, `ListSubagentsAsync`, `GetSubagentMessagesAsync`, `RenameSessionAsync`,
`TagSessionAsync`, `DeleteSessionAsync`, `ForkSessionAsync`) has a `*FromStoreAsync`/`*ViaStoreAsync`
counterpart backed by an `ISessionStore` (e.g. `InMemorySessionStore`, or your own adapter) instead
of the local `~/.claude/projects` filesystem layout.

A conversation can be disconnected and picked back up later with `ClaudeAgentOptions.Resume =
<sessionId>` — the CLI subprocess always persists its transcript to local disk first (controllable
via the `CLAUDE_CONFIG_DIR` environment variable); an `ISessionStore` adapter, if configured,
receives a secondary mirrored copy for external/durable storage. See `Sessions/` for the full
mirroring/resume/store story.

## Structured output

```csharp
var result = messages.OfType<ResultMessage>().Last();
var typed = result.GetStructuredOutput(MyJsonContext.Default.MyResultType); // JsonTypeInfo<T>, AOT-safe
```

Pass `OutputFormat = OutputFormats.JsonSchemaFor(MyJsonContext.Default.MyResultType)` (or
`JsonSchema<T>()` for the reflection-based convenience form) on `ClaudeAgentOptions` to ask the CLI
to constrain its output to that schema in the first place.

## CLI metadata

```csharp
var version = await ClaudeCli.GetVersionAsync();          // ClaudeCliMetadata
var status = await ClaudeCli.GetUpdateStatusAsync();       // ClaudeCliUpdateStatus
```

Not part of the Python SDK — an extra convenience carried over from the C# reference SDK this port
reused code from (see `NOTICE.md`).

## Python → .NET name mapping

| Python (`claude_agent_sdk`) | .NET (`BD.Connectors.ClaudeCode`) |
|---|---|
| `query()` | `ClaudeAgent.QueryAsync()` |
| `ClaudeSDKClient` | `ClaudeSdkClient` |
| `ClaudeAgentOptions` | `ClaudeAgentOptions` (same fields, `PascalCase`) |
| `Transport` (ABC) | `ITransport` |
| `Message`, `UserMessage`, `AssistantMessage`, `SystemMessage`, `ResultMessage`, `StreamEvent`, `RateLimitEvent`, `ConversationResetMessage` | same names, under `Messages/` |
| `TextBlock`, `ThinkingBlock`, `ToolUseBlock`, `ToolResultBlock`, `ServerToolUseBlock`, `ServerToolResultBlock` | same names, under `Messages/` |
| `CanUseTool`, `PermissionResultAllow`, `PermissionResultDeny`, `ToolPermissionContext` | `CanUseToolCallback`, same result/context names, under `Permissions/` |
| `HookCallback`, `HookMatcher`, `HookContext`, `*HookInput`, `HookJSONOutput` | same names (`HookJsonOutput`), under `Hooks/` |
| `tool()`, `create_sdk_mcp_server()`, `SdkMcpTool`, `ToolAnnotations` | `SdkMcp.Tool()`, `SdkMcp.CreateServer()`, same type names, under `Mcp/` |
| `list_sessions`, `rename_session`, `fork_session`, … | `ClaudeSessions.ListSessionsAsync()`, `RenameSessionAsync()`, `ForkSessionAsync()`, … |
| `SessionStore`, `InMemorySessionStore`, `fold_session_summary`, `project_key_for_directory` | `ISessionStore`, `InMemorySessionStore`, `SessionSummary.Fold()`, `ClaudeSessions.ProjectKeyForDirectory()` |
| `ClaudeSDKError` → `ResultError` hierarchy | `ClaudeSdkException` → `ResultException` hierarchy, under `Errors/` |


## Versioning and stability

This package targets behavioral parity with `claude-agent-sdk-python` 0.2.159. It is early (`0.x`):
the public API follows the Python SDK's shape closely and is not expected to churn, but breaking
changes may still land before `1.0.0`.

## Known limitations

- `ClaudeAgentOptions.User` (run the subprocess as another OS user) has no .NET equivalent and was
  dropped, matching the Python SDK's own Unix-only caveat.
- `debug_stderr` (deprecated in the Python SDK) was not ported.
- No `Microsoft.Extensions.AI` `IChatClient` adapter and no `ISessionStore` conformance test suite
  for third-party adapter authors — both are optional add-ons the porting plan deferred.
  `InMemorySessionStore` itself is fully implemented and tested.

## Examples

`examples/BD.Connectors.ClaudeCode.Examples/` has one runnable console example per topic
(quick start, streaming mode, tool permission callback, hooks, MCP calculator, agents, system
prompt, max budget, partial messages, stderr callback, setting sources, tools option, plugins):

```bash
dotnet run --project examples/BD.Connectors.ClaudeCode.Examples -- quick-start
dotnet run --project examples/BD.Connectors.ClaudeCode.Examples -- all
```

## Building and testing

```bash
dotnet build BD.Connectors.ClaudeCode.slnx -c Release -warnaserror
dotnet test --solution BD.Connectors.ClaudeCode.slnx -c Release -- --treenode-filter "/*/*/*/*[TestKind=Unit]"
dotnet test --solution BD.Connectors.ClaudeCode.slnx -c Release -- --treenode-filter "/*/*/*/*[TestKind=CliSmoke]"
```

`TestKind=E2E` tests need a real, authenticated `claude` CLI session and make real (cheap, `haiku`)
model calls — run them deliberately, never in CI.

## License

MIT (`LICENSE`). This project ports the behavior of two other MIT-licensed projects; see
`NOTICE.md` for their license text and attribution.
