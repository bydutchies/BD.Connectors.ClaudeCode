using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Permissions;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class PermissionUpdateTests
{
    [Test]
    public async Task ToJson_AddRules_AlwaysIncludesRuleContent()
    {
        var update = new PermissionUpdate(PermissionUpdateType.AddRules)
        {
            Rules = [new PermissionRuleValue("Bash", null), new PermissionRuleValue("Read", "docs/*")],
            Behavior = PermissionBehavior.Allow,
            Destination = PermissionUpdateDestination.Session,
        };

        var json = update.ToJson();

        await Assert.That(json["type"]!.GetValue<string>()).IsEqualTo("addRules");
        await Assert.That(json["destination"]!.GetValue<string>()).IsEqualTo("session");
        await Assert.That(json["behavior"]!.GetValue<string>()).IsEqualTo("allow");
        var rules = json["rules"]!.AsArray();
        await Assert.That(rules.Count).IsEqualTo(2);
        await Assert.That(rules[0]!["toolName"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(rules[0]!["ruleContent"]).IsNull();
        await Assert.That(rules[0]!.AsObject().ContainsKey("ruleContent")).IsTrue();
        await Assert.That(rules[1]!["ruleContent"]!.GetValue<string>()).IsEqualTo("docs/*");
    }

    [Test]
    public async Task ToJson_SetMode_WritesModeOnly()
    {
        var update = new PermissionUpdate(PermissionUpdateType.SetMode) { Mode = PermissionMode.AcceptEdits };

        var json = update.ToJson();

        await Assert.That(json["type"]!.GetValue<string>()).IsEqualTo("setMode");
        await Assert.That(json["mode"]!.GetValue<string>()).IsEqualTo("acceptEdits");
        await Assert.That(json.ContainsKey("rules")).IsFalse();
    }

    [Test]
    public async Task ToJson_AddDirectories_WritesDirectoriesArray()
    {
        var update = new PermissionUpdate(PermissionUpdateType.AddDirectories) { Directories = ["/x", "/y"] };

        var json = update.ToJson();

        var directories = json["directories"]!.AsArray();
        await Assert.That(directories.Count).IsEqualTo(2);
        await Assert.That(directories[0]!.GetValue<string>()).IsEqualTo("/x");
    }

    [Test]
    public async Task FromJson_RoundTripsAddRules()
    {
        var data = (JsonObject)JsonNode.Parse("""
            {"type":"addRules","destination":"session","behavior":"allow",
             "rules":[{"toolName":"Bash","ruleContent":null}]}
            """)!;

        var update = PermissionUpdate.FromJson(data);

        await Assert.That(update.Type).IsEqualTo(PermissionUpdateType.AddRules);
        await Assert.That(update.Destination).IsEqualTo(PermissionUpdateDestination.Session);
        await Assert.That(update.Behavior).IsEqualTo(PermissionBehavior.Allow);
        await Assert.That(update.Rules!.Count).IsEqualTo(1);
        await Assert.That(update.Rules![0].ToolName).IsEqualTo("Bash");
        await Assert.That(update.Rules![0].RuleContent).IsNull();
    }
}
