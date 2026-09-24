using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Hooks;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class HookJsonOutputTests
{
    [Test]
    public async Task AsyncHookJsonOutput_WritesAsyncTrue()
    {
        var output = new AsyncHookJsonOutput(AsyncTimeoutMs: 5000);

        var json = output.ToJson();

        await Assert.That(json["async"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json["asyncTimeout"]!.GetValue<int>()).IsEqualTo(5000);
    }

    [Test]
    public async Task SyncHookJsonOutput_OmitsNullFields()
    {
        var output = new SyncHookJsonOutput { Continue = false, StopReason = "blocked" };

        var json = output.ToJson();

        await Assert.That(json["continue"]!.GetValue<bool>()).IsFalse();
        await Assert.That(json["stopReason"]!.GetValue<string>()).IsEqualTo("blocked");
        await Assert.That(json.ContainsKey("decision")).IsFalse();
        await Assert.That(json.ContainsKey("hookSpecificOutput")).IsFalse();
    }

    [Test]
    public async Task SyncHookJsonOutput_WritesHookSpecificOutput()
    {
        var output = new SyncHookJsonOutput
        {
            HookSpecificOutput = new PreToolUseHookSpecificOutput(PermissionDecision: "deny", PermissionDecisionReason: "blocked path"),
        };

        var json = output.ToJson();

        var specific = json["hookSpecificOutput"]!.AsObject();
        await Assert.That(specific["hookEventName"]!.GetValue<string>()).IsEqualTo("PreToolUse");
        await Assert.That(specific["permissionDecision"]!.GetValue<string>()).IsEqualTo("deny");
    }

    [Test]
    public async Task RawHookJsonOutput_ConvertsPythonSafeKeys()
    {
        var raw = new JsonObject { ["async_"] = true, ["continue_"] = false, ["stopReason"] = "x" };
        var output = new RawHookJsonOutput(raw);

        var json = output.ToJson();

        await Assert.That(json["async"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json["continue"]!.GetValue<bool>()).IsFalse();
        await Assert.That(json["stopReason"]!.GetValue<string>()).IsEqualTo("x");
        await Assert.That(json.ContainsKey("async_")).IsFalse();
        await Assert.That(json.ContainsKey("continue_")).IsFalse();
    }
}
