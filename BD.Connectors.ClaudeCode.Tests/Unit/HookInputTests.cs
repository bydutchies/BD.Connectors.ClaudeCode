using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Hooks;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class HookInputTests
{
    [Test]
    public async Task FromJson_PreToolUse_PopulatesToolFields()
    {
        var data = Parse("""
            {"hook_event_name":"PreToolUse","session_id":"s1","transcript_path":"/t","cwd":"/cwd",
             "tool_name":"Bash","tool_input":{"command":"ls"},"tool_use_id":"tu1"}
            """);

        var input = HookInput.FromJson(data);

        await Assert.That(input).IsTypeOf<PreToolUseHookInput>();
        var preToolUse = (PreToolUseHookInput)input;
        await Assert.That(preToolUse.ToolName).IsEqualTo("Bash");
        await Assert.That(preToolUse.ToolUseId).IsEqualTo("tu1");
        await Assert.That(preToolUse.SessionId).IsEqualTo("s1");
        await Assert.That(preToolUse.HookEventName).IsEqualTo("PreToolUse");
    }

    [Test]
    public async Task FromJson_UnknownEvent_YieldsUnknownHookInput()
    {
        var data = Parse("""{"hook_event_name":"SomethingNew","session_id":"s1"}""");

        var input = HookInput.FromJson(data);

        await Assert.That(input).IsTypeOf<UnknownHookInput>();
        await Assert.That(input.SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task FromJson_MissingFields_NeverThrows()
    {
        var data = Parse("{}");

        var input = HookInput.FromJson(data);

        await Assert.That(input.SessionId).IsEqualTo(string.Empty);
        await Assert.That(input.HookEventName).IsEqualTo(string.Empty);
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
