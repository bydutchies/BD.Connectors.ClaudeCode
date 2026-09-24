using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Mcp;

public static class McpServerConnectionStatuses
{
    public const string Connected = "connected";
    public const string Failed = "failed";
    public const string NeedsAuth = "needs-auth";
    public const string Pending = "pending";
    public const string Disabled = "disabled";
}

public sealed record McpToolAnnotations
{
    public bool? ReadOnly { get; init; }

    public bool? Destructive { get; init; }

    public bool? OpenWorld { get; init; }

    public static McpToolAnnotations FromJson(JsonObject data) => new()
    {
        ReadOnly = JsonHelpers.GetBool(data, "readOnly"),
        Destructive = JsonHelpers.GetBool(data, "destructive"),
        OpenWorld = JsonHelpers.GetBool(data, "openWorld"),
    };
}

public sealed record McpToolInfo(string Name)
{
    public string? Description { get; init; }

    public McpToolAnnotations? Annotations { get; init; }

    public static McpToolInfo FromJson(JsonObject data) => new(JsonHelpers.GetRequiredString(data, "name"))
    {
        Description = JsonHelpers.GetString(data, "description"),
        Annotations = JsonHelpers.GetObject(data, "annotations") is { } annotations ? McpToolAnnotations.FromJson(annotations) : null,
    };
}

public sealed record McpServerInfo(string Name, string Version)
{
    public static McpServerInfo FromJson(JsonObject data) => new(
        JsonHelpers.GetRequiredString(data, "name"),
        JsonHelpers.GetRequiredString(data, "version"));
}

// Config stays a raw pass-through: it is a union across stdio/sse/http/sdk/claudeai-proxy shapes.
public sealed record McpServerStatus(string Name, string Status)
{
    public McpServerInfo? ServerInfo { get; init; }

    public string? Error { get; init; }

    public JsonObject? Config { get; init; }

    public string? Scope { get; init; }

    public IReadOnlyList<McpToolInfo>? Tools { get; init; }

    public static McpServerStatus FromJson(JsonObject data)
    {
        var tools = JsonHelpers.GetArray(data, "tools");
        return new McpServerStatus(JsonHelpers.GetRequiredString(data, "name"), JsonHelpers.GetRequiredString(data, "status"))
        {
            ServerInfo = JsonHelpers.GetObject(data, "serverInfo") is { } serverInfo ? McpServerInfo.FromJson(serverInfo) : null,
            Error = JsonHelpers.GetString(data, "error"),
            Config = JsonHelpers.GetObject(data, "config"),
            Scope = JsonHelpers.GetString(data, "scope"),
            Tools = tools?.OfType<JsonObject>().Select(McpToolInfo.FromJson).ToList(),
        };
    }
}

public sealed record McpStatusResponse(IReadOnlyList<McpServerStatus> McpServers)
{
    public static McpStatusResponse FromJson(JsonObject data)
    {
        var servers = JsonHelpers.GetArray(data, "mcpServers")?.OfType<JsonObject>().Select(McpServerStatus.FromJson).ToList() ?? [];
        return new McpStatusResponse(servers);
    }
}

public sealed record ContextUsageCategory(string Name, long Tokens, string Color)
{
    public bool? IsDeferred { get; init; }

    public static ContextUsageCategory FromJson(JsonObject data) => new(
        JsonHelpers.GetRequiredString(data, "name"),
        JsonHelpers.GetInt64(data, "tokens") ?? 0,
        JsonHelpers.GetRequiredString(data, "color"))
    {
        IsDeferred = JsonHelpers.GetBool(data, "isDeferred"),
    };
}

public sealed record ContextUsageResponse(
    IReadOnlyList<ContextUsageCategory> Categories,
    long TotalTokens,
    long MaxTokens,
    long RawMaxTokens,
    double Percentage,
    string Model,
    bool IsAutoCompactEnabled)
{
    public JsonObject Raw { get; init; } = [];

    public static ContextUsageResponse FromJson(JsonObject data)
    {
        var categories = JsonHelpers.GetArray(data, "categories")?.OfType<JsonObject>().Select(ContextUsageCategory.FromJson).ToList() ?? [];
        return new ContextUsageResponse(
            categories,
            JsonHelpers.GetInt64(data, "totalTokens") ?? 0,
            JsonHelpers.GetInt64(data, "maxTokens") ?? 0,
            JsonHelpers.GetInt64(data, "rawMaxTokens") ?? 0,
            JsonHelpers.GetDouble(data, "percentage") ?? 0,
            JsonHelpers.GetString(data, "model") ?? string.Empty,
            JsonHelpers.GetBool(data, "isAutoCompactEnabled") ?? false)
        {
            Raw = data,
        };
    }
}
