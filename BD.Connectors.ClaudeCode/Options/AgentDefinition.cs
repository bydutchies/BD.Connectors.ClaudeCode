using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Options;

public sealed record AgentDefinition(string Description, string Prompt)
{
    public IReadOnlyList<string>? Tools { get; init; }

    public IReadOnlyList<string>? DisallowedTools { get; init; }

    // Model alias ("sonnet", "opus", "haiku", "inherit") or a full model id.
    public string? Model { get; init; }

    public IReadOnlyList<string>? Skills { get; init; }

    public AgentMemory? Memory { get; init; }

    // Each entry is a server name, or an inline server-config object.
    public IReadOnlyList<JsonNode>? McpServers { get; init; }

    public string? InitialPrompt { get; init; }

    public int? MaxTurns { get; init; }

    public bool? Background { get; init; }

    // EffortLevel or a raw integer, per PY's `EffortLevel | int | None`.
    public JsonNode? Effort { get; init; }

    public PermissionMode? PermissionMode { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["description"] = Description,
            ["prompt"] = Prompt,
        };

        JsonHelpers.SetStringArray(json, "tools", Tools);
        JsonHelpers.SetStringArray(json, "disallowedTools", DisallowedTools);
        if (Model is not null)
        {
            json["model"] = Model;
        }

        JsonHelpers.SetStringArray(json, "skills", Skills);
        if (Memory is { } memory)
        {
            json["memory"] = memory.ToWireValue();
        }

        if (McpServers is not null)
        {
            json["mcpServers"] = new JsonArray(McpServers.Select(server => (JsonNode?)server.DeepClone()).ToArray());
        }

        if (InitialPrompt is not null)
        {
            json["initialPrompt"] = InitialPrompt;
        }

        if (MaxTurns is { } maxTurns)
        {
            json["maxTurns"] = maxTurns;
        }

        if (Background is { } background)
        {
            json["background"] = background;
        }

        if (Effort is not null)
        {
            json["effort"] = Effort.DeepClone();
        }

        if (PermissionMode is { } permissionMode)
        {
            json["permissionMode"] = permissionMode.ToWireValue();
        }

        return json;
    }
}
