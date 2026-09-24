using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Hooks;

public abstract record HookJsonOutput
{
    public abstract JsonObject ToJson();
}

public sealed record AsyncHookJsonOutput(int? AsyncTimeoutMs = null) : HookJsonOutput
{
    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["async"] = true };
        if (AsyncTimeoutMs is { } timeout)
        {
            json["asyncTimeout"] = timeout;
        }

        return json;
    }
}

public sealed record SyncHookJsonOutput : HookJsonOutput
{
    public bool? Continue { get; init; }

    public bool? SuppressOutput { get; init; }

    public string? StopReason { get; init; }

    public string? Decision { get; init; }

    public string? SystemMessage { get; init; }

    public string? Reason { get; init; }

    public HookSpecificOutput? HookSpecificOutput { get; init; }

    public override JsonObject ToJson()
    {
        var json = new JsonObject();

        if (Continue is { } continueValue)
        {
            json["continue"] = continueValue;
        }

        if (SuppressOutput is { } suppressOutput)
        {
            json["suppressOutput"] = suppressOutput;
        }

        if (StopReason is not null)
        {
            json["stopReason"] = StopReason;
        }

        if (Decision is not null)
        {
            json["decision"] = Decision;
        }

        if (SystemMessage is not null)
        {
            json["systemMessage"] = SystemMessage;
        }

        if (Reason is not null)
        {
            json["reason"] = Reason;
        }

        if (HookSpecificOutput is not null)
        {
            json["hookSpecificOutput"] = HookSpecificOutput.ToJson();
        }

        return json;
    }
}

// Escape hatch: forwards an arbitrary hook-output object unchanged, applying only the
// async_/continue_ -> async/continue rename the CLI expects (PY's _convert_hook_output_for_cli).
public sealed record RawHookJsonOutput(JsonObject Json) : HookJsonOutput
{
    public override JsonObject ToJson()
    {
        var converted = new JsonObject();
        foreach (var (key, value) in Json)
        {
            var convertedKey = key switch
            {
                "async_" => "async",
                "continue_" => "continue",
                _ => key,
            };
            converted[convertedKey] = value?.DeepClone();
        }

        return converted;
    }
}
