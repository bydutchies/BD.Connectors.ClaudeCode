using System.Text;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class LineFramerTests
{
    [Test]
    public async Task Push_SingleChunkWithOneLine_ReturnsCompletedLine()
    {
        var framer = new LineFramer();

        var lines = PushText(framer, "hello\n");

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("hello");
        await Assert.That(framer.PendingLength).IsEqualTo(0);
    }

    [Test]
    public async Task Push_NoNewline_BuffersAsPending()
    {
        var framer = new LineFramer();

        var lines = PushText(framer, "partial");

        await Assert.That(lines.Count).IsEqualTo(0);
        await Assert.That(framer.PendingLength).IsEqualTo(7);
    }

    [Test]
    public async Task Push_MultipleLinesInOneChunk_ReturnsAllInOrder()
    {
        var framer = new LineFramer();

        var lines = PushText(framer, "one\ntwo\nthree\n");

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[0]).IsEqualTo("one");
        await Assert.That(lines[1]).IsEqualTo("two");
        await Assert.That(lines[2]).IsEqualTo("three");
    }

    [Test]
    public async Task Push_LineSplitAcrossChunkBoundary_ReassemblesCorrectly()
    {
        var framer = new LineFramer();

        var first = PushText(framer, "{\"type\":\"mess");
        var second = PushText(framer, "age\"}\n");

        await Assert.That(first.Count).IsEqualTo(0);
        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That(second[0]).IsEqualTo("{\"type\":\"message\"}");
    }

    [Test]
    public async Task Push_MultiByteUtf8CharacterSplitAcrossChunkBoundary_DecodesCorrectly()
    {
        // "café" - the "é" (U+00E9) encodes as two UTF-8 bytes; split the chunk between them.
        var bytes = Encoding.UTF8.GetBytes("café\n");
        var framer = new LineFramer();

        var splitIndex = bytes.Length - 2; // inside the two-byte encoding of "é"
        var firstChunk = bytes[..splitIndex];
        var secondChunk = bytes[splitIndex..];

        var first = framer.Push(firstChunk, firstChunk.Length);
        var second = framer.Push(secondChunk, secondChunk.Length);

        await Assert.That(first.Count).IsEqualTo(0);
        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(second[0])).IsEqualTo("café");
    }

    [Test]
    public async Task Push_CarriageReturnBeforeNewline_IsKeptInTheLine()
    {
        // The framer only splits on raw "\n"; trimming "\r" is the caller's job (matches PY, which
        // strips a full decoded line rather than special-casing CRLF while framing).
        var framer = new LineFramer();

        var lines = PushText(framer, "hello\r\n");

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("hello\r");
    }

    [Test]
    public async Task Flush_WithPendingPartialLine_ReturnsItAndClearsBuffer()
    {
        var framer = new LineFramer();
        PushText(framer, "trailing without newline");

        var tail = framer.Flush();

        await Assert.That(Encoding.UTF8.GetString(tail)).IsEqualTo("trailing without newline");
        await Assert.That(framer.PendingLength).IsEqualTo(0);
    }

    [Test]
    public async Task Flush_WithNoPending_ReturnsEmpty()
    {
        var framer = new LineFramer();

        var tail = framer.Flush();

        await Assert.That(tail.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Push_LargeLineAcrossManySmallChunks_ReassemblesWholeLine()
    {
        var framer = new LineFramer();
        var payload = string.Concat(Enumerable.Repeat("0123456789", 10_000)); // 100_000 chars, no newline
        var fullLine = payload + "\n";
        var bytes = Encoding.UTF8.GetBytes(fullLine);

        const int chunkSize = 37; // deliberately not a divisor of the payload length
        IReadOnlyList<byte[]> lastResult = [];
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = bytes[offset..(offset + count)];
            var result = framer.Push(chunk, chunk.Length);
            if (result.Count > 0)
            {
                lastResult = result;
            }
        }

        await Assert.That(lastResult.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(lastResult[0])).IsEqualTo(payload);
    }

    [Test]
    public async Task Push_EmptyLines_AreReturnedAsEmptyByteArrays()
    {
        var framer = new LineFramer();

        var lines = PushText(framer, "\n\n");

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo(string.Empty);
        await Assert.That(lines[1]).IsEqualTo(string.Empty);
    }

    private static List<string> PushText(LineFramer framer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [.. framer.Push(bytes, bytes.Length).Select(Encoding.UTF8.GetString)];
    }
}
