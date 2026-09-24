using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Mcp;

namespace BD.Connectors.ClaudeCode.Options;

public abstract record McpServerConfig
{
    public abstract JsonObject ToJson();

    protected static JsonObject BuildHttpLike(string type, string url, IReadOnlyDictionary<string, string>? headers)
    {
        var json = new JsonObject
        {
            ["type"] = type,
            ["url"] = url,
        };

        if (headers is not null)
        {
            var headersJson = new JsonObject();
            foreach (var (key, value) in headers)
            {
                headersJson[key] = value;
            }

            json["headers"] = headersJson;
        }

        return json;
    }
}

public sealed record McpStdioServerConfig(string Command, IReadOnlyList<string>? Args = null, IReadOnlyDictionary<string, string>? Env = null)
    : McpServerConfig
{
    public override JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = Command,
        };

        JsonHelpers.SetStringArray(json, "args", Args);
        if (Env is not null)
        {
            var envJson = new JsonObject();
            foreach (var (key, value) in Env)
            {
                envJson[key] = value;
            }

            json["env"] = envJson;
        }

        return json;
    }
}

public sealed record McpSseServerConfig(string Url, IReadOnlyDictionary<string, string>? Headers = null) : McpServerConfig
{
    public override JsonObject ToJson() => BuildHttpLike("sse", Url, Headers);
}

public sealed record McpHttpServerConfig(string Url, IReadOnlyDictionary<string, string>? Headers = null) : McpServerConfig
{
    public override JsonObject ToJson() => BuildHttpLike("http", Url, Headers);
}

// Instance is intentionally left out of the wire form (it runs in-process; see F5).
public sealed record McpSdkServerConfig(string Name, ISdkMcpServer Instance) : McpServerConfig
{
    public override JsonObject ToJson() => new()
    {
        ["type"] = "sdk",
        ["name"] = Name,
    };
}
