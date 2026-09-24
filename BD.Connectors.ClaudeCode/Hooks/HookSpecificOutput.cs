using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Hooks;

public abstract record HookSpecificOutput(string HookEventName)
{
    public abstract JsonObject ToJson();

    protected JsonObject BaseJson()
    {
        return new JsonObject { ["hookEventName"] = HookEventName };
    }
}

public sealed record PreToolUseHookSpecificOutput(
    string? PermissionDecision = null,
    string? PermissionDecisionReason = null,
    JsonObject? UpdatedInput = null,
    string? AdditionalContext = null) : HookSpecificOutput("PreToolUse")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        if (PermissionDecision is not null)
        {
            json["permissionDecision"] = PermissionDecision;
        }

        if (PermissionDecisionReason is not null)
        {
            json["permissionDecisionReason"] = PermissionDecisionReason;
        }

        if (UpdatedInput is not null)
        {
            json["updatedInput"] = UpdatedInput.DeepClone();
        }

        if (AdditionalContext is not null)
        {
            json["additionalContext"] = AdditionalContext;
        }

        return json;
    }
}

public sealed record PostToolUseHookSpecificOutput(
    string? AdditionalContext = null,
    JsonNode? UpdatedToolOutput = null,
    JsonNode? UpdatedMcpToolOutput = null) : HookSpecificOutput("PostToolUse")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        if (AdditionalContext is not null)
        {
            json["additionalContext"] = AdditionalContext;
        }

        if (UpdatedToolOutput is not null)
        {
            json["updatedToolOutput"] = UpdatedToolOutput.DeepClone();
        }

        if (UpdatedMcpToolOutput is not null)
        {
            json["updatedMCPToolOutput"] = UpdatedMcpToolOutput.DeepClone();
        }

        return json;
    }
}

public sealed record PostToolUseFailureHookSpecificOutput(string? AdditionalContext = null)
    : HookSpecificOutput("PostToolUseFailure")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        if (AdditionalContext is not null)
        {
            json["additionalContext"] = AdditionalContext;
        }

        return json;
    }
}

public sealed record UserPromptSubmitHookSpecificOutput(string? AdditionalContext = null)
    : HookSpecificOutput("UserPromptSubmit")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        if (AdditionalContext is not null)
        {
            json["additionalContext"] = AdditionalContext;
        }

        return json;
    }
}

public sealed record SessionStartHookSpecificOutput(string? AdditionalContext = null)
    : HookSpecificOutput("SessionStart")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        if (AdditionalContext is not null)
        {
            json["additionalContext"] = AdditionalContext;
        }

        return json;
    }
}

public sealed record NotificationHookSpecificOutput(string? AdditionalContext = null)
    : HookSpecificOutput("Notification")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        if (AdditionalContext is not null)
        {
            json["additionalContext"] = AdditionalContext;
        }

        return json;
    }
}

public sealed record SubagentStartHookSpecificOutput(string? AdditionalContext = null)
    : HookSpecificOutput("SubagentStart")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        if (AdditionalContext is not null)
        {
            json["additionalContext"] = AdditionalContext;
        }

        return json;
    }
}

public sealed record PermissionRequestHookSpecificOutput(JsonObject Decision) : HookSpecificOutput("PermissionRequest")
{
    public override JsonObject ToJson()
    {
        var json = BaseJson();
        json["decision"] = Decision.DeepClone();
        return json;
    }
}
