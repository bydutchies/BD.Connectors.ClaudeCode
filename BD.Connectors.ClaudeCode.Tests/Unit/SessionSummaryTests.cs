using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Sessions;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// SessionSummary.Fold / SummaryEntryToSdkInfo, ported from PY.
[Property("TestKind", "Unit")]
public class SessionSummaryTests
{
    private static readonly SessionKey Key = new("proj", "session-1");

    private static JsonObject UserEntry(string text, string? timestamp = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };
        if (timestamp is not null)
        {
            obj["timestamp"] = timestamp;
        }

        return obj;
    }

    [Test]
    public async Task Fold_FirstUserMessage_BecomesFirstPromptAndLocks()
    {
        var folded = SessionSummary.Fold(null, Key, [UserEntry("hello world")]);

        await Assert.That(folded.Data["first_prompt"]!.ToString()).IsEqualTo("hello world");
        await Assert.That(folded.Data["first_prompt_locked"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Fold_LaterUserMessage_DoesNotOverwriteLockedFirstPrompt()
    {
        var first = SessionSummary.Fold(null, Key, [UserEntry("first")]);

        var second = SessionSummary.Fold(first, Key, [UserEntry("second")]);

        await Assert.That(second.Data["first_prompt"]!.ToString()).IsEqualTo("first");
    }

    [Test]
    public async Task Fold_SlashCommand_StoresCommandFallback_NotFirstPrompt()
    {
        var entry = UserEntry("<command-name>review</command-name> some args");

        var folded = SessionSummary.Fold(null, Key, [entry]);

        await Assert.That(folded.Data.ContainsKey("first_prompt")).IsFalse();
        await Assert.That(folded.Data["command_fallback"]!.ToString()).IsEqualTo("review");
    }

    [Test]
    public async Task Fold_ToolResultUserMessage_IsSkipped()
    {
        var entry = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray([new JsonObject { ["type"] = "tool_result", ["content"] = "x" }]),
            },
        };

        var folded = SessionSummary.Fold(null, Key, [entry]);

        await Assert.That(folded.Data.ContainsKey("first_prompt")).IsFalse();
    }

    [Test]
    public async Task Fold_LastWinsFields_OverwriteAcrossCalls()
    {
        var entryA = new JsonObject { ["type"] = "system", ["uuid"] = "a", ["customTitle"] = "Title A" };
        var entryB = new JsonObject { ["type"] = "system", ["uuid"] = "b", ["customTitle"] = "Title B" };

        var first = SessionSummary.Fold(null, Key, [entryA]);
        var second = SessionSummary.Fold(first, Key, [entryB]);

        await Assert.That(second.Data["custom_title"]!.ToString()).IsEqualTo("Title B");
    }

    [Test]
    public async Task Fold_TagEntry_SetsThenClearsOnEmptyString()
    {
        var setTag = new JsonObject { ["type"] = "tag", ["uuid"] = "a", ["tag"] = "important" };
        var clearTag = new JsonObject { ["type"] = "tag", ["uuid"] = "b", ["tag"] = string.Empty };

        var afterSet = SessionSummary.Fold(null, Key, [setTag]);
        await Assert.That(afterSet.Data["tag"]!.ToString()).IsEqualTo("important");

        var afterClear = SessionSummary.Fold(afterSet, Key, [clearTag]);
        await Assert.That(afterClear.Data.ContainsKey("tag")).IsFalse();
    }

    [Test]
    public async Task Fold_CreatedAt_LatchesFirstParseableTimestamp()
    {
        var e1 = UserEntry("one", "2024-01-01T00:00:00Z");
        var e2 = UserEntry("two", "2024-06-01T00:00:00Z");

        var folded = SessionSummary.Fold(null, Key, [e1, e2]);

        var expected = DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        await Assert.That(folded.Data["created_at"]!.GetValue<long>()).IsEqualTo(expected);
    }

    [Test]
    public async Task Fold_MtimeIsNotTouchedByFold()
    {
        var prev = new SessionSummaryEntry("session-1", 12345, []);

        var folded = SessionSummary.Fold(prev, Key, [UserEntry("hi")]);

        await Assert.That(folded.Mtime).IsEqualTo(12345);
    }

    [Test]
    public async Task Fold_NewSession_MtimePlaceholderIsZero()
    {
        var folded = SessionSummary.Fold(null, Key, [UserEntry("hi")]);

        await Assert.That(folded.Mtime).IsEqualTo(0);
    }

    [Test]
    public async Task Fold_IsSidechain_LatchesFromFirstEntry()
    {
        var entry = new JsonObject { ["type"] = "user", ["uuid"] = "a", ["isSidechain"] = true };

        var folded = SessionSummary.Fold(null, Key, [entry]);

        await Assert.That(folded.Data["is_sidechain"]!.GetValue<bool>()).IsTrue();
    }

    // ---------------------------------------------------------------------
    // SummaryEntryToSdkInfo
    // ---------------------------------------------------------------------

    [Test]
    public async Task SummaryEntryToSdkInfo_Sidechain_ReturnsNull()
    {
        var folded = SessionSummary.Fold(null, Key, [new JsonObject { ["type"] = "user", ["uuid"] = "a", ["isSidechain"] = true }]);

        var info = SessionSummary.SummaryEntryToSdkInfo(folded, projectPath: null);

        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task SummaryEntryToSdkInfo_NoExtractableSummary_ReturnsNull()
    {
        var folded = SessionSummary.Fold(null, Key, [new JsonObject { ["type"] = "system", ["uuid"] = "a" }]);

        var info = SessionSummary.SummaryEntryToSdkInfo(folded, projectPath: null);

        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task SummaryEntryToSdkInfo_CustomTitleWinsOverFirstPrompt()
    {
        var folded = SessionSummary.Fold(null, Key, [
            UserEntry("first prompt"),
            new JsonObject { ["type"] = "system", ["uuid"] = "b", ["customTitle"] = "Custom" },
        ]);

        var info = SessionSummary.SummaryEntryToSdkInfo(folded with { Mtime = 42 }, projectPath: null);

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Summary).IsEqualTo("Custom");
        await Assert.That(info.FirstPrompt).IsEqualTo("first prompt");
        await Assert.That(info.LastModified).IsEqualTo(42);
        await Assert.That(info.FileSize).IsNull();
    }
}
