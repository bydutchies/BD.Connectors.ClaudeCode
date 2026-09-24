using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Permissions;

public sealed record PermissionRuleValue(string ToolName, string? RuleContent = null);

public sealed record PermissionUpdate(PermissionUpdateType Type)
{
    public IReadOnlyList<PermissionRuleValue>? Rules { get; init; }

    public PermissionBehavior? Behavior { get; init; }

    public PermissionMode? Mode { get; init; }

    public IReadOnlyList<string>? Directories { get; init; }

    public PermissionUpdateDestination? Destination { get; init; }

    public JsonObject ToJson()
    {
        var result = new JsonObject { ["type"] = Type.ToWireValue() };

        if (Destination is { } destination)
        {
            result["destination"] = destination.ToWireValue();
        }

        switch (Type)
        {
            case PermissionUpdateType.AddRules or PermissionUpdateType.ReplaceRules or PermissionUpdateType.RemoveRules:
                if (Rules is not null)
                {
                    var ruleNodes = Rules.Select(rule => (JsonNode?)new JsonObject
                    {
                        ["toolName"] = rule.ToolName,
                        ["ruleContent"] = rule.RuleContent,
                    }).ToArray();
                    result["rules"] = new JsonArray(ruleNodes);
                }

                if (Behavior is { } behavior)
                {
                    result["behavior"] = behavior.ToWireValue();
                }

                break;

            case PermissionUpdateType.SetMode:
                if (Mode is { } mode)
                {
                    result["mode"] = mode.ToWireValue();
                }

                break;

            case PermissionUpdateType.AddDirectories or PermissionUpdateType.RemoveDirectories:
                if (Directories is not null)
                {
                    var directoryNodes = Directories.Select(directory => (JsonNode?)JsonValue.Create(directory)).ToArray();
                    result["directories"] = new JsonArray(directoryNodes);
                }

                break;
        }

        return result;
    }

    public static PermissionUpdate FromJson(JsonObject data)
    {
        List<PermissionRuleValue>? rules = null;
        if (JsonHelpers.GetArray(data, "rules") is { } rawRules)
        {
            rules = [];
            foreach (var ruleNode in rawRules)
            {
                var rule = (JsonObject)ruleNode!;
                rules.Add(new PermissionRuleValue(JsonHelpers.GetRequiredString(rule, "toolName"), JsonHelpers.GetString(rule, "ruleContent")));
            }
        }

        var type = WireValues.PermissionUpdateTypeFromWireValue(JsonHelpers.GetRequiredString(data, "type"))
            ?? throw new ArgumentException($"Unknown permission update type: '{JsonHelpers.GetString(data, "type")}'", nameof(data));

        return new PermissionUpdate(type)
        {
            Rules = rules,
            Behavior = WireValues.PermissionBehaviorFromWireValue(JsonHelpers.GetString(data, "behavior")),
            Mode = WireValues.PermissionModeFromWireValue(JsonHelpers.GetString(data, "mode")),
            Directories = JsonHelpers.GetArray(data, "directories")?.Select(node => node!.GetValue<string>()).ToList(),
            Destination = WireValues.PermissionUpdateDestinationFromWireValue(JsonHelpers.GetString(data, "destination")),
        };
    }
}
