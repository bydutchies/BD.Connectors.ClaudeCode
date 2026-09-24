using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Options;

public sealed record SandboxNetworkConfig
{
    public IReadOnlyList<string>? AllowedDomains { get; init; }

    public IReadOnlyList<string>? DeniedDomains { get; init; }

    public bool? AllowManagedDomainsOnly { get; init; }

    public IReadOnlyList<string>? AllowUnixSockets { get; init; }

    public bool? AllowAllUnixSockets { get; init; }

    public bool? AllowLocalBinding { get; init; }

    public IReadOnlyList<string>? AllowMachLookup { get; init; }

    public int? HttpProxyPort { get; init; }

    public int? SocksProxyPort { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        JsonHelpers.SetStringArray(json, "allowedDomains", AllowedDomains);
        JsonHelpers.SetStringArray(json, "deniedDomains", DeniedDomains);
        if (AllowManagedDomainsOnly is { } allowManagedDomainsOnly)
        {
            json["allowManagedDomainsOnly"] = allowManagedDomainsOnly;
        }

        JsonHelpers.SetStringArray(json, "allowUnixSockets", AllowUnixSockets);
        if (AllowAllUnixSockets is { } allowAllUnixSockets)
        {
            json["allowAllUnixSockets"] = allowAllUnixSockets;
        }

        if (AllowLocalBinding is { } allowLocalBinding)
        {
            json["allowLocalBinding"] = allowLocalBinding;
        }

        JsonHelpers.SetStringArray(json, "allowMachLookup", AllowMachLookup);
        if (HttpProxyPort is { } httpProxyPort)
        {
            json["httpProxyPort"] = httpProxyPort;
        }

        if (SocksProxyPort is { } socksProxyPort)
        {
            json["socksProxyPort"] = socksProxyPort;
        }

        return json;
    }
}

public sealed record SandboxIgnoreViolations
{
    public IReadOnlyList<string>? File { get; init; }

    public IReadOnlyList<string>? Network { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        JsonHelpers.SetStringArray(json, "file", File);
        JsonHelpers.SetStringArray(json, "network", Network);
        return json;
    }
}

public sealed record SandboxSettings
{
    public bool? Enabled { get; init; }

    public bool? AutoAllowBashIfSandboxed { get; init; }

    public IReadOnlyList<string>? ExcludedCommands { get; init; }

    public bool? AllowUnsandboxedCommands { get; init; }

    public SandboxNetworkConfig? Network { get; init; }

    public SandboxIgnoreViolations? IgnoreViolations { get; init; }

    public bool? EnableWeakerNestedSandbox { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Enabled is { } enabled)
        {
            json["enabled"] = enabled;
        }

        if (AutoAllowBashIfSandboxed is { } autoAllowBashIfSandboxed)
        {
            json["autoAllowBashIfSandboxed"] = autoAllowBashIfSandboxed;
        }

        JsonHelpers.SetStringArray(json, "excludedCommands", ExcludedCommands);
        if (AllowUnsandboxedCommands is { } allowUnsandboxedCommands)
        {
            json["allowUnsandboxedCommands"] = allowUnsandboxedCommands;
        }

        if (Network is not null)
        {
            json["network"] = Network.ToJson();
        }

        if (IgnoreViolations is not null)
        {
            json["ignoreViolations"] = IgnoreViolations.ToJson();
        }

        if (EnableWeakerNestedSandbox is { } enableWeakerNestedSandbox)
        {
            json["enableWeakerNestedSandbox"] = enableWeakerNestedSandbox;
        }

        return json;
    }
}
