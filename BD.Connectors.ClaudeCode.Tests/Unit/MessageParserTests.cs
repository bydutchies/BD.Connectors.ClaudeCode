using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Messages;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class MessageParserTests
{
    [Test]
    public async Task User_TextBlock_Parses()
    {
        var data = Parse("""{"type":"user","message":{"content":[{"type":"text","text":"Hello"}]}}""");

        var message = MessageParser.Parse(data);

        await Assert.That(message).IsTypeOf<UserMessage>();
        var user = (UserMessage)message!;
        await Assert.That(user.BlockContent).IsNotNull();
        await Assert.That(user.BlockContent!.Count).IsEqualTo(1);
        await Assert.That(((TextBlock)user.BlockContent[0]).Text).IsEqualTo("Hello");
    }

    [Test]
    public async Task User_StringContent_SetsTextContent()
    {
        var data = Parse("""{"type":"user","message":{"content":"Simple string content"}}""");

        var message = (UserMessage)MessageParser.Parse(data)!;

        await Assert.That(message.TextContent).IsEqualTo("Simple string content");
        await Assert.That(message.BlockContent).IsNull();
    }

    [Test]
    public async Task User_MixedContentBlocks_ParseInOrder()
    {
        var data = Parse("""
            {"type":"user","message":{"content":[
                {"type":"text","text":"a"},
                {"type":"tool_use","id":"u1","name":"Search","input":{"q":"x"}},
                {"type":"tool_result","tool_use_id":"u1","content":"results","is_error":false},
                {"type":"text","text":"b"}
            ]}}
            """);

        var message = (UserMessage)MessageParser.Parse(data)!;

        await Assert.That(message.BlockContent!.Count).IsEqualTo(4);
        await Assert.That(message.BlockContent[0]).IsTypeOf<TextBlock>();
        await Assert.That(message.BlockContent[1]).IsTypeOf<ToolUseBlock>();
        await Assert.That(message.BlockContent[2]).IsTypeOf<ToolResultBlock>();
        await Assert.That(message.BlockContent[3]).IsTypeOf<TextBlock>();
    }

    [Test]
    public async Task User_Origin_PeerKind_IsSurfaced()
    {
        var data = Parse("""
            {"type":"user","message":{"content":"hi"},"origin":{"kind":"peer","from":"peer-addr","someFutureField":true}}
            """);

        var message = (UserMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Origin).IsNotNull();
        await Assert.That(message.Origin!["kind"]!.GetValue<string>()).IsEqualTo("peer");
        await Assert.That(message.Origin["from"]!.GetValue<string>()).IsEqualTo("peer-addr");
    }

    [Test]
    public async Task User_Origin_AbsentOrMalformed_IsNull()
    {
        string[] payloads =
        [
            """{"type":"user","message":{"content":"hi"}}""",
            """{"type":"user","message":{"content":"hi"},"origin":null}""",
            """{"type":"user","message":{"content":"hi"},"origin":"human"}""",
            """{"type":"user","message":{"content":"hi"},"origin":{}}""",
        ];

        foreach (var payload in payloads)
        {
            var message = (UserMessage)MessageParser.Parse(Parse(payload))!;
            await Assert.That(message.Origin).IsNull();
        }
    }

    [Test]
    public async Task User_ToolUseResult_IsPassedThrough()
    {
        var data = Parse("""
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"done"}]},
             "tool_use_result":{"filePath":"/f.py","oldString":"a"}}
            """);

        var message = (UserMessage)MessageParser.Parse(data)!;

        await Assert.That(message.ToolUseResult).IsNotNull();
        await Assert.That(message.ToolUseResult!["filePath"]!.GetValue<string>()).IsEqualTo("/f.py");
    }

    [Test]
    public async Task User_MissingFields_Throws()
    {
        var data = Parse("""{"type":"user"}""");

        var exception = await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
        await Assert.That(exception!.Message).Contains("Missing required field in user message");
    }

    [Test]
    public async Task Assistant_ValidMessage_Parses()
    {
        var data = Parse("""{"type":"assistant","message":{"model":"m","content":[{"type":"text","text":"Hi"}]}}""");

        var message = (AssistantMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Model).IsEqualTo("m");
        await Assert.That(((TextBlock)message.Content[0]).Text).IsEqualTo("Hi");
    }

    [Test]
    public async Task Assistant_Thinking_Parses()
    {
        var data = Parse("""
            {"type":"assistant","message":{"model":"m","content":[
                {"type":"thinking","thinking":"reasoning","signature":"sig"}
            ]}}
            """);

        var message = (AssistantMessage)MessageParser.Parse(data)!;

        var block = (ThinkingBlock)message.Content[0];
        await Assert.That(block.Thinking).IsEqualTo("reasoning");
        await Assert.That(block.Signature).IsEqualTo("sig");
    }

    [Test]
    public async Task Assistant_ServerToolUse_Parses()
    {
        var data = Parse("""
            {"type":"assistant","message":{"model":"m","content":[
                {"type":"server_tool_use","id":"s1","name":"web_search","input":{"query":"x"}}
            ]}}
            """);

        var message = (AssistantMessage)MessageParser.Parse(data)!;

        var block = (ServerToolUseBlock)message.Content[0];
        await Assert.That(block.Id).IsEqualTo("s1");
        await Assert.That(block.Name).IsEqualTo("web_search");
    }

    [Test]
    public async Task Assistant_AdvisorToolResult_MapsToServerToolResultBlock()
    {
        var data = Parse("""
            {"type":"assistant","message":{"model":"m","content":[
                {"type":"advisor_tool_result","tool_use_id":"a1","content":{"type":"text"}}
            ]}}
            """);

        var message = (AssistantMessage)MessageParser.Parse(data)!;

        var block = (ServerToolResultBlock)message.Content[0];
        await Assert.That(block.ToolUseId).IsEqualTo("a1");
    }

    [Test]
    public async Task Assistant_StringContent_Throws()
    {
        var data = Parse("""{"type":"assistant","message":{"model":"m","content":"hi"}}""");

        await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
    }

    [Test]
    public async Task Assistant_NonDictContentBlock_Throws()
    {
        var data = Parse("""{"type":"assistant","message":{"model":"m","content":["oops"]}}""");

        await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
    }

    [Test]
    public async Task User_NonDictContentBlock_Throws()
    {
        var data = Parse("""{"type":"user","message":{"content":["oops"]}}""");

        await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
    }

    [Test]
    public async Task Assistant_MissingFields_Throws()
    {
        var data = Parse("""{"type":"assistant"}""");

        var exception = await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
        await Assert.That(exception!.Message).Contains("Missing required field in assistant message");
    }

    [Test]
    public async Task System_Generic_Parses()
    {
        var data = Parse("""{"type":"system","subtype":"some_future_subtype","foo":"bar"}""");

        var message = MessageParser.Parse(data)!;

        await Assert.That(message.GetType()).IsEqualTo(typeof(SystemMessage));
        await Assert.That(((SystemMessage)message).Subtype).IsEqualTo("some_future_subtype");
    }

    [Test]
    public async Task System_MissingFields_Throws()
    {
        var data = Parse("""{"type":"system"}""");

        var exception = await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
        await Assert.That(exception!.Message).Contains("Missing required field in system message");
    }

    [Test]
    public async Task TaskStarted_Parses()
    {
        var data = Parse("""
            {"type":"system","subtype":"task_started","task_id":"t1","description":"desc","uuid":"u1","session_id":"s1"}
            """);

        var message = (TaskStartedMessage)MessageParser.Parse(data)!;

        await Assert.That(message).IsAssignableTo<SystemMessage>();
        await Assert.That(message.TaskId).IsEqualTo("t1");
        await Assert.That(message.Subtype).IsEqualTo("task_started");
        await Assert.That(message.Data).IsEqualTo(data);
    }

    [Test]
    public async Task TaskProgress_Parses()
    {
        var data = Parse("""
            {"type":"system","subtype":"task_progress","task_id":"t1","description":"desc",
             "usage":{"total_tokens":1,"tool_uses":0,"duration_ms":10},"uuid":"u2","session_id":"s1"}
            """);

        var message = (TaskProgressMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Usage.TotalTokens).IsEqualTo(1L);
        await Assert.That(message.Usage.DurationMs).IsEqualTo(10L);
    }

    [Test]
    public async Task TaskNotification_Parses()
    {
        var data = Parse("""
            {"type":"system","subtype":"task_notification","task_id":"t1","status":"stopped",
             "output_file":"/o","summary":"s","uuid":"u3","session_id":"s1"}
            """);

        var message = (TaskNotificationMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Status).IsEqualTo("stopped");
        await Assert.That(message.Usage).IsNull();
    }

    [Test]
    public async Task TaskUpdated_TerminalStatus_IsSurfaced()
    {
        var data = Parse("""
            {"type":"system","subtype":"task_updated","task_id":"task-abc",
             "patch":{"status":"completed","end_time":1780405729183},"uuid":"uuid-4","session_id":"session-1"}
            """);

        var message = (TaskUpdatedMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Status).IsEqualTo("completed");
        await Assert.That(TaskStatuses.Terminal.Contains(message.Status!)).IsTrue();
    }

    [Test]
    public async Task TaskUpdated_NoPatch_DefaultsToEmpty()
    {
        var data = Parse("""{"type":"system","subtype":"task_updated","task_id":"task-abc"}""");

        var message = (TaskUpdatedMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Patch.Count).IsEqualTo(0);
        await Assert.That(message.Status).IsNull();
    }

    [Test]
    public async Task TaskUpdated_NonDictPatch_NeverThrows()
    {
        var data = Parse("""{"type":"system","subtype":"task_updated","task_id":"task-abc","patch":"completed"}""");

        var message = (TaskUpdatedMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Patch.Count).IsEqualTo(0);
        await Assert.That(message.Status).IsNull();
    }

    [Test]
    public async Task MirrorError_Parses()
    {
        var data = Parse("""{"type":"system","subtype":"mirror_error","error":"boom"}""");

        var message = (MirrorErrorMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Error).IsEqualTo("boom");
        await Assert.That(message.Key).IsNull();
    }

    [Test]
    public async Task Result_ValidMessage_Parses()
    {
        var data = Parse("""
            {"type":"result","subtype":"success","duration_ms":3000,"duration_api_ms":2000,
             "is_error":false,"num_turns":1,"session_id":"s1","result":"Hello"}
            """);

        var message = (ResultMessage)MessageParser.Parse(data)!;

        await Assert.That(message.DurationMs).IsEqualTo(3000L);
        await Assert.That(message.IsError).IsFalse();
        await Assert.That(message.Result).IsEqualTo("Hello");
    }

    [Test]
    public async Task Result_ModelUsageAndPermissionDenials_ArePreserved()
    {
        var data = Parse("""
            {"type":"result","subtype":"success","duration_ms":1,"duration_api_ms":1,
             "is_error":false,"num_turns":1,"session_id":"s1",
             "modelUsage":{"claude-x":{"costUSD":0.01}},"permission_denials":[],
             "uuid":"u1"}
            """);

        var message = (ResultMessage)MessageParser.Parse(data)!;

        await Assert.That(message.ModelUsage).IsNotNull();
        await Assert.That(message.PermissionDenials).IsNotNull();
        await Assert.That(message.Uuid).IsEqualTo("u1");
    }

    [Test]
    public async Task Result_DeferredToolUse_Parses()
    {
        var data = Parse("""
            {"type":"result","subtype":"success","duration_ms":1,"duration_api_ms":1,
             "is_error":false,"num_turns":1,"session_id":"s1",
             "deferred_tool_use":{"id":"toolu_1","name":"Bash","input":{"command":"ls"}}}
            """);

        var message = (ResultMessage)MessageParser.Parse(data)!;

        await Assert.That(message.DeferredToolUse).IsNotNull();
        await Assert.That(message.DeferredToolUse!.Name).IsEqualTo("Bash");
    }

    [Test]
    public async Task Result_Errors_ArePreserved()
    {
        var data = Parse("""
            {"type":"result","subtype":"error_during_execution","duration_ms":5000,"duration_api_ms":3000,
             "is_error":true,"num_turns":3,"session_id":"s1",
             "errors":["Tool execution failed: permission denied","Unable to write to /etc/hosts"]}
            """);

        var message = (ResultMessage)MessageParser.Parse(data)!;

        await Assert.That(message.Errors!.Count).IsEqualTo(2);
        await Assert.That(message.Errors![0]).IsEqualTo("Tool execution failed: permission denied");
        await Assert.That(message.Errors![1]).IsEqualTo("Unable to write to /etc/hosts");
    }

    [Test]
    public async Task Result_MissingFields_Throws()
    {
        var data = Parse("""{"type":"result","subtype":"success"}""");

        var exception = await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
        await Assert.That(exception!.Message).Contains("Missing required field in result message");
    }

    [Test]
    public async Task RateLimitEvent_Parses()
    {
        var data = Parse("""
            {"type":"rate_limit_event","uuid":"u1","session_id":"s1",
             "rate_limit_info":{"status":"allowed_warning","resetsAt":123,"rateLimitType":"five_hour","utilization":0.9}}
            """);

        var message = (RateLimitEvent)MessageParser.Parse(data)!;

        await Assert.That(message.RateLimitInfo.Status).IsEqualTo("allowed_warning");
        await Assert.That(message.RateLimitInfo.ResetsAt).IsEqualTo(123L);
        await Assert.That(message.RateLimitInfo.Utilization).IsEqualTo(0.9);
    }

    [Test]
    public async Task ConversationReset_Parses()
    {
        var data = Parse("""{"type":"conversation_reset","new_conversation_id":"c1","uuid":"u1","session_id":"s1"}""");

        var message = (ConversationResetMessage)MessageParser.Parse(data)!;

        await Assert.That(message.NewConversationId).IsEqualTo("c1");
    }

    [Test]
    public async Task ConversationReset_MissingField_Throws()
    {
        var data = Parse("""{"type":"conversation_reset","uuid":"u","session_id":"s"}""");

        var exception = await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
        await Assert.That(exception!.Message).Contains("new_conversation_id");
    }

    [Test]
    public async Task MissingTypeField_Throws()
    {
        var data = Parse("""{"message":{"content":[]}}""");

        var exception = await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
        await Assert.That(exception!.Message).Contains("Message missing 'type' field");
    }

    [Test]
    public async Task UnknownMessageType_ReturnsNull()
    {
        var data = Parse("""{"type":"unknown_type"}""");

        var message = MessageParser.Parse(data);

        await Assert.That(message).IsNull();
    }

    [Test]
    public async Task HookEvent_Started_Parses()
    {
        var data = Parse("""
            {"type":"system","subtype":"hook_started","hook_event":"PreToolUse","hook_name":"PreToolUse",
             "session_id":"sess-123","uuid":"uuid-456","tool_name":"Bash"}
            """);

        var message = (HookEventMessage)MessageParser.Parse(data)!;

        await Assert.That(message).IsAssignableTo<SystemMessage>();
        await Assert.That(message.HookEventName).IsEqualTo("PreToolUse");
        await Assert.That(message.SessionId).IsEqualTo("sess-123");
    }

    [Test]
    public async Task HookEvent_Minimal_Parses()
    {
        var data = Parse("""{"type":"system","subtype":"hook_started","hook_name":"Stop"}""");

        var message = (HookEventMessage)MessageParser.Parse(data)!;

        await Assert.That(message.HookEventName).IsEqualTo("Stop");
        await Assert.That(message.SessionId).IsNull();
    }

    [Test]
    public async Task MessageParseException_ContainsPayload()
    {
        var data = Parse("""{"type":"assistant"}""");

        var exception = await Assert.That(() => MessageParser.Parse(data)).ThrowsExactly<MessageParseException>();
        await Assert.That(exception!.Payload).IsEqualTo(data);
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
