using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Messages;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Not a PY port -- BD.Connectors.ClaudeCode.Messages.ClaudeResponse is a C#-only convenience type.
[Property("TestKind", "Unit")]
public class ClaudeResponseTests
{
    private static AssistantMessage Assistant(string model, params ContentBlock[] blocks) =>
        new(blocks, model);

    private static UserMessage User(params ContentBlock[] blocks) =>
        new() { BlockContent = blocks };

    private static ResultMessage Result(
        string sessionId = "session-1",
        bool isError = false,
        long durationMs = 100,
        long durationApiMs = 90,
        int numTurns = 1,
        string? stopReason = null,
        double? totalCostUsd = null,
        IReadOnlyList<string>? errors = null) =>
        new("success", durationMs, durationApiMs, isError, numTurns, sessionId)
        {
            StopReason = stopReason,
            TotalCostUsd = totalCostUsd,
            Errors = errors,
        };

    [Test]
    public async Task Messages_ReturnsExactlyWhatWasPassedIn()
    {
        var assistant = Assistant("claude-test", new TextBlock("Hi"));
        var result = Result();

        var response = new ClaudeResponse([assistant, result]);

        await Assert.That(response.Messages.Count).IsEqualTo(2);
        await Assert.That(response.Messages[0]).IsSameReferenceAs(assistant);
        await Assert.That(response.Messages[1]).IsSameReferenceAs(result);
    }

    [Test]
    public async Task UserAssistantSystemMessages_FilterByType()
    {
        var user = User(new TextBlock("hello"));
        var assistant = Assistant("claude-test", new TextBlock("hi"));
        var system = new SystemMessage("init", []);
        var result = Result();

        var response = new ClaudeResponse([user, assistant, system, result]);

        await Assert.That(response.UserMessages.Count).IsEqualTo(1);
        await Assert.That(response.UserMessages[0]).IsSameReferenceAs(user);
        await Assert.That(response.AssistantMessages.Count).IsEqualTo(1);
        await Assert.That(response.AssistantMessages[0]).IsSameReferenceAs(assistant);
        await Assert.That(response.SystemMessages.Count).IsEqualTo(1);
        await Assert.That(response.SystemMessages[0]).IsSameReferenceAs(system);
    }

    [Test]
    public async Task Result_ReturnsTheResultMessage()
    {
        var result = Result(sessionId: "session-42");

        var response = new ClaudeResponse([Assistant("claude-test", new TextBlock("hi")), result]);

        await Assert.That(response.Result).IsSameReferenceAs(result);
    }

    [Test]
    public async Task Constructor_NoResultMessageInStream_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = new ClaudeResponse([Assistant("claude-test", new TextBlock("hi"))]);
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task Text_ConcatenatesTextBlocksAcrossAssistantMessages_InOrder()
    {
        var response = new ClaudeResponse(
        [
            Assistant("claude-test", new TextBlock("Hello, ")),
            User(new TextBlock("(ignored, not an assistant message)")),
            Assistant("claude-test", new TextBlock("world!")),
            Result(),
        ]);

        await Assert.That(response.Text).IsEqualTo("Hello, world!");
    }

    [Test]
    public async Task ToolUses_CollectsToolUseBlocksFromAssistantMessages_InOrder()
    {
        var first = new ToolUseBlock("id-1", "Read", []);
        var second = new ToolUseBlock("id-2", "Write", []);

        var response = new ClaudeResponse(
        [
            Assistant("claude-test", new TextBlock("using tools"), first),
            Assistant("claude-test", second),
            Result(),
        ]);

        await Assert.That(response.ToolUses.Count).IsEqualTo(2);
        await Assert.That(response.ToolUses[0]).IsSameReferenceAs(first);
        await Assert.That(response.ToolUses[1]).IsSameReferenceAs(second);
    }

    [Test]
    public async Task ToolResults_CollectsToolResultBlocksFromUserMessages()
    {
        var toolResult = new ToolResultBlock("id-1", JsonValue.Create("file contents"));

        var response = new ClaudeResponse(
        [
            Assistant("claude-test", new ToolUseBlock("id-1", "Read", [])),
            User(toolResult),
            Result(),
        ]);

        await Assert.That(response.ToolResults.Count).IsEqualTo(1);
        await Assert.That(response.ToolResults[0]).IsSameReferenceAs(toolResult);
    }

    [Test]
    public async Task PassthroughProperties_ReflectTheResultMessage()
    {
        var result = Result(
            sessionId: "session-99",
            isError: true,
            durationMs: 1234,
            durationApiMs: 1000,
            numTurns: 3,
            stopReason: "end_turn",
            totalCostUsd: 0.0456,
            errors: ["boom"]);

        var response = new ClaudeResponse([result]);

        await Assert.That(response.SessionId).IsEqualTo("session-99");
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.DurationMs).IsEqualTo(1234);
        await Assert.That(response.DurationApiMs).IsEqualTo(1000);
        await Assert.That(response.NumTurns).IsEqualTo(3);
        await Assert.That(response.StopReason).IsEqualTo("end_turn");
        await Assert.That(response.TotalCostUsd).IsEqualTo(0.0456);
        await Assert.That(response.Errors!.Count).IsEqualTo(1);
        await Assert.That(response.Errors![0]).IsEqualTo("boom");
    }

    [Test]
    public async Task Model_ReturnsModelOfLastAssistantMessage()
    {
        var response = new ClaudeResponse(
        [
            Assistant("claude-first", new TextBlock("a")),
            Assistant("claude-second", new TextBlock("b")),
            Result(),
        ]);

        await Assert.That(response.Model).IsEqualTo("claude-second");
    }

    [Test]
    public async Task Model_NoAssistantMessages_ReturnsNull()
    {
        var response = new ClaudeResponse([Result()]);

        await Assert.That(response.Model).IsNull();
    }
}
