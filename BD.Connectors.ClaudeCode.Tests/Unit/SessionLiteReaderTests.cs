using BD.Connectors.ClaudeCode.Sessions.Internal;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class SessionLiteReaderTests
{
    [Test]
    public async Task ExtractJsonStringField_Simple()
    {
        const string text = "{\"foo\":\"bar\",\"baz\":\"qux\"}";

        await Assert.That(SessionLiteReader.ExtractJsonStringField(text, "foo")).IsEqualTo("bar");
        await Assert.That(SessionLiteReader.ExtractJsonStringField(text, "baz")).IsEqualTo("qux");
        await Assert.That(SessionLiteReader.ExtractJsonStringField(text, "missing")).IsNull();
    }

    [Test]
    public async Task ExtractJsonStringField_HandlesEscapedQuotesAndSpaceAfterColon()
    {
        const string text = "{\"title\": \"say \\\"hi\\\"\"}";

        await Assert.That(SessionLiteReader.ExtractJsonStringField(text, "title")).IsEqualTo("say \"hi\"");
    }

    [Test]
    public async Task ExtractLastJsonStringField_ReturnsLastOccurrence()
    {
        const string text = "{\"tag\":\"first\"}\n{\"tag\":\"second\"}\n{\"tag\":\"third\"}";

        await Assert.That(SessionLiteReader.ExtractLastJsonStringField(text, "tag")).IsEqualTo("third");
    }

    [Test]
    public async Task ExtractLastJsonStringField_NoMatch_ReturnsNull()
    {
        await Assert.That(SessionLiteReader.ExtractLastJsonStringField("{}", "tag")).IsNull();
    }

    [Test]
    public async Task ExtractFirstPromptFromHead_ReturnsFirstUserText()
    {
        var head = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Hello Claude\"}}\n"
            + "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":\"Hi!\"}}";

        var result = SessionLiteReader.ExtractFirstPromptFromHead(head);

        await Assert.That(result).IsEqualTo("Hello Claude");
    }

    [Test]
    public async Task ExtractFirstPromptFromHead_SkipsToolResultAndIsMetaLines()
    {
        var head = "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"meta stuff\"}}\n"
            + "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"text\":\"ignored\"}]},\"tool_result\":true}\n"
            + "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"real prompt\"}}";

        var result = SessionLiteReader.ExtractFirstPromptFromHead(head);

        await Assert.That(result).IsEqualTo("real prompt");
    }

    [Test]
    public async Task ExtractFirstPromptFromHead_TruncatesLongPromptTo200Chars()
    {
        var longPrompt = new string('a', 250);
        var head = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + longPrompt + "\"}}";

        var result = SessionLiteReader.ExtractFirstPromptFromHead(head);

        await Assert.That(result.Length).IsEqualTo(201); // 200 chars + the ellipsis character
        await Assert.That(result).EndsWith("…");
    }

    [Test]
    public async Task ExtractFirstPromptFromHead_NoUserMessage_ReturnsEmpty()
    {
        var head = "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":\"Hi!\"}}";

        var result = SessionLiteReader.ExtractFirstPromptFromHead(head);

        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ParseSessionInfoFromLite_SidechainFirstLine_ReturnsNull()
    {
        var head = "{\"type\":\"user\",\"isSidechain\":true,\"message\":{\"role\":\"user\",\"content\":\"hi\"}}";
        var lite = new LiteSessionFile { Mtime = 1000, Size = head.Length, Head = head, Tail = head };

        var result = SessionLiteReader.ParseSessionInfoFromLite("550e8400-e29b-41d4-a716-446655440000", lite);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseSessionInfoFromLite_NoExtractableSummary_ReturnsNull()
    {
        var head = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"\"}}";
        var lite = new LiteSessionFile { Mtime = 1000, Size = head.Length, Head = head, Tail = head };

        var result = SessionLiteReader.ParseSessionInfoFromLite("550e8400-e29b-41d4-a716-446655440000", lite);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseSessionInfoFromLite_CustomTitleWinsOverFirstPrompt()
    {
        var head = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Hello Claude\"}}";
        var tail = "{\"type\":\"custom-title\",\"customTitle\":\"My Session\"}";
        var lite = new LiteSessionFile { Mtime = 1000, Size = head.Length + tail.Length, Head = head, Tail = tail };

        var result = SessionLiteReader.ParseSessionInfoFromLite("550e8400-e29b-41d4-a716-446655440000", lite);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Summary).IsEqualTo("My Session");
        await Assert.That(result.CustomTitle).IsEqualTo("My Session");
        await Assert.That(result.FirstPrompt).IsEqualTo("Hello Claude");
    }
}
