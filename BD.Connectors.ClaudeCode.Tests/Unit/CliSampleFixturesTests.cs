using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class CliSampleFixturesTests
{
    [Test]
    public async Task AllSampleLines_ParseWithoutException()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-samples.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        var nonEmptyLines = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();

        await Assert.That(nonEmptyLines.Count).IsGreaterThan(0);

        foreach (var line in nonEmptyLines)
        {
            var data = (JsonObject)JsonNode.Parse(line)!;
            var message = MessageParser.Parse(data);
            await Assert.That(message).IsNotNull();
        }
    }
}
