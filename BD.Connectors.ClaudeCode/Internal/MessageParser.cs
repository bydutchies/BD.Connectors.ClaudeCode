using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Messages;

namespace BD.Connectors.ClaudeCode.Internal;

internal static class MessageParser
{
    internal static Message? Parse(JsonObject data)
    {
        var type = JsonHelpers.GetString(data, WireConstants.Keys.Type);
        var subtype = JsonHelpers.GetString(data, WireConstants.Keys.Subtype);

        // Hook events (include_hook_events) arrive as system messages; route them before
        // the generic 'type' presence check and the switch below.
        if (type == WireConstants.MessageTypes.System
            && (subtype == WireConstants.SystemSubtypes.HookStarted || subtype == WireConstants.SystemSubtypes.HookResponse))
        {
            var hookEventName = JsonHelpers.GetString(data, WireConstants.Keys.HookEvent)
                ?? JsonHelpers.GetString(data, WireConstants.Keys.HookName)
                ?? JsonHelpers.GetString(data, WireConstants.Keys.HookEventName)
                ?? string.Empty;
            return new HookEventMessage(
                subtype!,
                data,
                hookEventName,
                JsonHelpers.GetString(data, WireConstants.Keys.SessionId),
                JsonHelpers.GetString(data, WireConstants.Keys.Uuid));
        }

        if (string.IsNullOrEmpty(type))
        {
            throw new MessageParseException("Message missing 'type' field", data);
        }

        switch (type)
        {
            case WireConstants.MessageTypes.User:
                return ParseUserMessage(data);

            case WireConstants.MessageTypes.Assistant:
                return ParseAssistantMessage(data);

            case WireConstants.MessageTypes.System:
                return ParseSystemMessage(data);

            case WireConstants.MessageTypes.Result:
                return ParseResultMessage(data);

            case WireConstants.MessageTypes.StreamEvent:
                return new StreamEvent(
                    RequireString(data, WireConstants.Keys.Uuid, data, "stream_event"),
                    RequireString(data, WireConstants.Keys.SessionId, data, "stream_event"),
                    RequireObjectField(data, WireConstants.Keys.Event, data, "stream_event"),
                    JsonHelpers.GetString(data, WireConstants.Keys.ParentToolUseId));

            case WireConstants.MessageTypes.RateLimitEvent:
                return ParseRateLimitEvent(data);

            case "conversation_reset":
                return new ConversationResetMessage(
                    RequireString(data, WireConstants.Keys.NewConversationId, data, "conversation_reset"),
                    RequireString(data, WireConstants.Keys.Uuid, data, "conversation_reset"),
                    RequireString(data, WireConstants.Keys.SessionId, data, "conversation_reset"));

            default:
                // Forward-compatible: unrecognized message types are skipped, not raised.
                return null;
        }
    }

    private static UserMessage ParseUserMessage(JsonObject data)
    {
        const string kind = "user";
        var messageObj = RequireObjectField(data, WireConstants.Keys.Message, data, kind);
        var contentNode = RequireField(messageObj, WireConstants.Keys.Content, data, kind);
        var parentToolUseId = JsonHelpers.GetString(data, WireConstants.Keys.ParentToolUseId);
        var toolUseResult = JsonHelpers.GetObject(data, WireConstants.Keys.ToolUseResult);
        var uuid = JsonHelpers.GetString(data, WireConstants.Keys.Uuid);
        var origin = ParseOrigin(data);

        if (contentNode is JsonArray blocks)
        {
            var contentBlocks = new List<ContentBlock>();
            foreach (var blockNode in blocks)
            {
                var block = RequireBlockObject(blockNode, data);
                var blockType = RequireString(block, WireConstants.Keys.Type, data, kind);
                var parsed = ParseSharedContentBlock(block, blockType, data, kind);
                if (parsed is not null)
                {
                    contentBlocks.Add(parsed);
                }
            }

            return new UserMessage
            {
                BlockContent = contentBlocks,
                Uuid = uuid,
                ParentToolUseId = parentToolUseId,
                ToolUseResult = toolUseResult,
                Origin = origin,
            };
        }

        return new UserMessage
        {
            TextContent = contentNode is JsonValue textValue && textValue.TryGetValue<string>(out var text) ? text : null,
            Uuid = uuid,
            ParentToolUseId = parentToolUseId,
            ToolUseResult = toolUseResult,
            Origin = origin,
        };
    }

    private static AssistantMessage ParseAssistantMessage(JsonObject data)
    {
        const string kind = "assistant";
        var messageObj = RequireObjectField(data, WireConstants.Keys.Message, data, kind);
        var contentNode = RequireField(messageObj, WireConstants.Keys.Content, data, kind);
        if (contentNode is not JsonArray blocks)
        {
            throw new MessageParseException($"Invalid assistant content (expected list, got {DescribeJsonType(contentNode)})", data);
        }

        var contentBlocks = new List<ContentBlock>();
        foreach (var blockNode in blocks)
        {
            var block = RequireBlockObject(blockNode, data);
            var blockType = RequireString(block, WireConstants.Keys.Type, data, kind);
            var parsed = ParseSharedContentBlock(block, blockType, data, kind) ?? ParseAssistantOnlyContentBlock(block, blockType, data, kind);
            if (parsed is not null)
            {
                contentBlocks.Add(parsed);
            }
        }

        return new AssistantMessage(contentBlocks, RequireString(messageObj, WireConstants.Keys.Model, data, kind))
        {
            ParentToolUseId = JsonHelpers.GetString(data, WireConstants.Keys.ParentToolUseId),
            Error = JsonHelpers.GetString(data, WireConstants.Keys.Error),
            Usage = JsonHelpers.GetObject(messageObj, WireConstants.Keys.Usage),
            MessageId = JsonHelpers.GetString(messageObj, WireConstants.Keys.Id),
            StopReason = JsonHelpers.GetString(messageObj, WireConstants.Keys.StopReason),
            SessionId = JsonHelpers.GetString(data, WireConstants.Keys.SessionId),
            Uuid = JsonHelpers.GetString(data, WireConstants.Keys.Uuid),
        };
    }

    private static ContentBlock? ParseSharedContentBlock(JsonObject block, string type, JsonObject data, string kind)
    {
        return type switch
        {
            WireConstants.ContentTypes.Text => new TextBlock(RequireString(block, "text", data, kind)),
            WireConstants.ContentTypes.ToolUse => new ToolUseBlock(
                RequireString(block, "id", data, kind),
                RequireString(block, WireConstants.Keys.Name, data, kind),
                RequireObjectField(block, WireConstants.Keys.Input, data, kind)),
            WireConstants.ContentTypes.ToolResult => new ToolResultBlock(
                RequireString(block, WireConstants.Keys.ToolUseId, data, kind),
                JsonHelpers.GetNode(block, WireConstants.Keys.Content),
                JsonHelpers.GetBool(block, WireConstants.Keys.IsError)),
            _ => null,
        };
    }

    private static ContentBlock? ParseAssistantOnlyContentBlock(JsonObject block, string type, JsonObject data, string kind)
    {
        return type switch
        {
            WireConstants.ContentTypes.Thinking => new ThinkingBlock(
                RequireString(block, "thinking", data, kind),
                RequireString(block, "signature", data, kind)),
            WireConstants.ContentTypes.ServerToolUse => new ServerToolUseBlock(
                RequireString(block, "id", data, kind),
                RequireString(block, WireConstants.Keys.Name, data, kind),
                RequireObjectField(block, WireConstants.Keys.Input, data, kind)),
            WireConstants.ContentTypes.AdvisorToolResult => new ServerToolResultBlock(
                RequireString(block, WireConstants.Keys.ToolUseId, data, kind),
                RequireObjectField(block, WireConstants.Keys.Content, data, kind)),
            _ => null,
        };
    }

    private static Message ParseSystemMessage(JsonObject data)
    {
        const string kind = "system";
        var subtype = RequireString(data, WireConstants.Keys.Subtype, data, kind);

        switch (subtype)
        {
            case WireConstants.SystemSubtypes.TaskStarted:
                return new TaskStartedMessage(
                    subtype,
                    data,
                    RequireString(data, WireConstants.Keys.TaskId, data, kind),
                    RequireString(data, WireConstants.Keys.Description, data, kind),
                    RequireString(data, WireConstants.Keys.Uuid, data, kind),
                    RequireString(data, WireConstants.Keys.SessionId, data, kind),
                    JsonHelpers.GetString(data, WireConstants.Keys.ToolUseId),
                    JsonHelpers.GetString(data, WireConstants.Keys.TaskType));

            case WireConstants.SystemSubtypes.TaskProgress:
                return new TaskProgressMessage(
                    subtype,
                    data,
                    RequireString(data, WireConstants.Keys.TaskId, data, kind),
                    RequireString(data, WireConstants.Keys.Description, data, kind),
                    ParseTaskUsage(RequireObjectField(data, WireConstants.Keys.Usage, data, kind)),
                    RequireString(data, WireConstants.Keys.Uuid, data, kind),
                    RequireString(data, WireConstants.Keys.SessionId, data, kind),
                    JsonHelpers.GetString(data, WireConstants.Keys.ToolUseId),
                    JsonHelpers.GetString(data, WireConstants.Keys.LastToolName));

            case WireConstants.SystemSubtypes.TaskNotification:
                return new TaskNotificationMessage(
                    subtype,
                    data,
                    RequireString(data, WireConstants.Keys.TaskId, data, kind),
                    RequireString(data, WireConstants.Keys.Status, data, kind),
                    RequireString(data, WireConstants.Keys.OutputFile, data, kind),
                    RequireString(data, WireConstants.Keys.Summary, data, kind),
                    RequireString(data, WireConstants.Keys.Uuid, data, kind),
                    RequireString(data, WireConstants.Keys.SessionId, data, kind),
                    JsonHelpers.GetString(data, WireConstants.Keys.ToolUseId),
                    JsonHelpers.GetObject(data, WireConstants.Keys.Usage) is { } usageObj ? ParseTaskUsage(usageObj) : null);

            case WireConstants.SystemSubtypes.TaskUpdated:
            {
                var patch = JsonHelpers.GetObject(data, WireConstants.Keys.Patch) ?? [];
                return new TaskUpdatedMessage(
                    subtype,
                    data,
                    JsonHelpers.GetString(data, WireConstants.Keys.TaskId) ?? string.Empty,
                    patch,
                    JsonHelpers.GetString(patch, WireConstants.Keys.Status),
                    JsonHelpers.GetString(data, WireConstants.Keys.SessionId),
                    JsonHelpers.GetString(data, WireConstants.Keys.Uuid));
            }

            case WireConstants.SystemSubtypes.MirrorError:
                return new MirrorErrorMessage(
                    subtype,
                    data,
                    JsonHelpers.GetObject(data, WireConstants.Keys.Key),
                    JsonHelpers.GetString(data, WireConstants.Keys.Error) ?? string.Empty);

            default:
                return new SystemMessage(subtype, data);
        }
    }

    private static ResultMessage ParseResultMessage(JsonObject data)
    {
        const string kind = "result";
        var deferredObj = JsonHelpers.GetObject(data, WireConstants.Keys.DeferredToolUse);

        return new ResultMessage(
            RequireString(data, WireConstants.Keys.Subtype, data, kind),
            RequireInt64(data, WireConstants.Keys.DurationMs, data, kind),
            RequireInt64(data, WireConstants.Keys.DurationApiMs, data, kind),
            RequireBool(data, WireConstants.Keys.IsError, data, kind),
            RequireInt32(data, WireConstants.Keys.NumTurns, data, kind),
            RequireString(data, WireConstants.Keys.SessionId, data, kind))
        {
            StopReason = JsonHelpers.GetString(data, WireConstants.Keys.StopReason),
            TotalCostUsd = JsonHelpers.GetDouble(data, WireConstants.Keys.TotalCostUsd),
            Usage = JsonHelpers.GetObject(data, WireConstants.Keys.Usage),
            Result = JsonHelpers.GetString(data, WireConstants.Keys.Result),
            StructuredOutput = JsonHelpers.GetNode(data, WireConstants.Keys.StructuredOutput),
            ModelUsage = JsonHelpers.GetObject(data, WireConstants.Keys.ModelUsage),
            PermissionDenials = JsonHelpers.GetArray(data, WireConstants.Keys.PermissionDenials),
            DeferredToolUse = deferredObj is { Count: > 0 }
                ? new DeferredToolUse(
                    RequireString(deferredObj, "id", data, kind),
                    RequireString(deferredObj, WireConstants.Keys.Name, data, kind),
                    RequireObjectField(deferredObj, WireConstants.Keys.Input, data, kind))
                : null,
            Errors = GetStringArrayOrNull(data, WireConstants.Keys.Errors),
            ApiErrorStatus = JsonHelpers.GetInt32(data, WireConstants.Keys.ApiErrorStatus),
            Uuid = JsonHelpers.GetString(data, WireConstants.Keys.Uuid),
            TerminalReason = JsonHelpers.GetString(data, WireConstants.Keys.TerminalReason),
            Origin = ParseOrigin(data),
        };
    }

    private static RateLimitEvent ParseRateLimitEvent(JsonObject data)
    {
        const string kind = "rate_limit_event";
        var info = RequireObjectField(data, WireConstants.Keys.RateLimitInfo, data, kind);
        return new RateLimitEvent(
            new RateLimitInfo(
                RequireString(info, WireConstants.Keys.Status, data, kind),
                JsonHelpers.GetInt64(info, WireConstants.Keys.ResetsAt),
                JsonHelpers.GetString(info, WireConstants.Keys.RateLimitType),
                JsonHelpers.GetDouble(info, WireConstants.Keys.Utilization),
                JsonHelpers.GetString(info, WireConstants.Keys.OverageStatus),
                JsonHelpers.GetInt64(info, WireConstants.Keys.OverageResetsAt),
                JsonHelpers.GetString(info, WireConstants.Keys.OverageDisabledReason),
                info),
            RequireString(data, WireConstants.Keys.Uuid, data, kind),
            RequireString(data, WireConstants.Keys.SessionId, data, kind));
    }

    private static TaskUsage ParseTaskUsage(JsonObject usage)
    {
        return new TaskUsage(
            JsonHelpers.GetInt64(usage, "total_tokens") ?? 0,
            JsonHelpers.GetInt64(usage, "tool_uses") ?? 0,
            JsonHelpers.GetInt64(usage, WireConstants.Keys.DurationMs) ?? 0);
    }

    private static JsonObject? ParseOrigin(JsonObject data)
    {
        var origin = JsonHelpers.GetObject(data, WireConstants.Keys.Origin);
        return origin is not null && origin.GetOriginKind() is not null ? origin : null;
    }

    private static List<string>? GetStringArrayOrNull(JsonObject data, string key)
    {
        var array = JsonHelpers.GetArray(data, key);
        if (array is null)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    private static JsonObject RequireBlockObject(JsonNode? blockNode, JsonObject data)
    {
        return blockNode as JsonObject
            ?? throw new MessageParseException($"Invalid content block (expected dict, got {DescribeJsonType(blockNode)})", data);
    }

    private static JsonNode? RequireField(JsonObject obj, string key, JsonObject topLevelData, string kind)
    {
        if (!obj.ContainsKey(key))
        {
            throw MissingField(topLevelData, kind, key);
        }

        return obj[key];
    }

    private static JsonObject RequireObjectField(JsonObject obj, string key, JsonObject topLevelData, string kind)
    {
        var node = RequireField(obj, key, topLevelData, kind);
        return node as JsonObject ?? throw MissingField(topLevelData, kind, key);
    }

    private static string RequireString(JsonObject obj, string key, JsonObject topLevelData, string kind)
    {
        var node = RequireField(obj, key, topLevelData, kind);
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString() ?? string.Empty;
    }

    private static long RequireInt64(JsonObject obj, string key, JsonObject topLevelData, string kind)
    {
        var node = RequireField(obj, key, topLevelData, kind);
        return node is JsonValue value && value.TryGetValue<long>(out var result) ? result : 0;
    }

    private static int RequireInt32(JsonObject obj, string key, JsonObject topLevelData, string kind)
    {
        var node = RequireField(obj, key, topLevelData, kind);
        return node is JsonValue value && value.TryGetValue<int>(out var result) ? result : 0;
    }

    private static bool RequireBool(JsonObject obj, string key, JsonObject topLevelData, string kind)
    {
        var node = RequireField(obj, key, topLevelData, kind);
        return node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private static MessageParseException MissingField(JsonObject topLevelData, string kind, string key)
    {
        return new MessageParseException($"Missing required field in {kind} message: '{key}'", topLevelData);
    }

    private static string DescribeJsonType(JsonNode? node)
    {
        return node switch
        {
            null => "NoneType",
            JsonObject => "dict",
            JsonArray => "list",
            JsonValue value when value.TryGetValue<bool>(out _) => "bool",
            JsonValue value when value.TryGetValue<string>(out _) => "str",
            JsonValue value when value.TryGetValue<long>(out _) => "int",
            JsonValue => "float",
            _ => "object",
        };
    }
}
