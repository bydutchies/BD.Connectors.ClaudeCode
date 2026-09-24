using BD.Connectors.ClaudeCode.Sessions.Internal;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class SessionPathsTests
{
    // Golden vectors from PY's own test suite.
    [Test]
    public async Task SanitizePath_Basic()
    {
        await Assert.That(SessionPaths.SanitizePath("/Users/foo/my-project")).IsEqualTo("-Users-foo-my-project");
        await Assert.That(SessionPaths.SanitizePath("plugin:name:server")).IsEqualTo("plugin-name-server");
    }

    [Test]
    public async Task SanitizePath_Long_TruncatesWithHashSuffix()
    {
        var longPath = string.Concat(Enumerable.Repeat("/x", 150)); // 300 chars

        var result = SessionPaths.SanitizePath(longPath);

        await Assert.That(result.Length).IsGreaterThan(200);
        await Assert.That(result).StartsWith("-x-x");
        await Assert.That(result[200..]).Contains('-');
    }

    // Surrogate-pair input: one emoji codepoint must become exactly one '-', matching Python's
    // per-codepoint iteration (see PORTING_STATUS.md Fase 6 note on SanitizePath).
    [Test]
    public async Task SanitizePath_SurrogatePairCodepoint_BecomesSingleHyphen()
    {
        var result = SessionPaths.SanitizePath("ab\U0001F600cd");

        await Assert.That(result).IsEqualTo("ab-cd");
    }

    [Test]
    public async Task SimpleHash_IsDeterministic()
    {
        await Assert.That(SessionPaths.SimpleHash("hello")).IsEqualTo(SessionPaths.SimpleHash("hello"));
        await Assert.That(SessionPaths.SimpleHash("hello")).IsNotEqualTo(SessionPaths.SimpleHash("world"));
    }

    [Test]
    public async Task SimpleHash_EmptyString_ReturnsZero()
    {
        await Assert.That(SessionPaths.SimpleHash(string.Empty)).IsEqualTo("0");
    }

    [Test]
    [Arguments("550e8400-e29b-41d4-a716-446655440000", true)]
    [Arguments("550E8400-E29B-41D4-A716-446655440000", true)] // case-insensitive
    [Arguments("not-a-uuid", false)]
    [Arguments("550e8400-e29b-41d4-a716-44665544000", false)] // too short
    [Arguments("", false)]
    public async Task ValidateUuid_AcceptsOnlyWellFormedUuids(string candidate, bool expectedValid)
    {
        var result = SessionPaths.ValidateUuid(candidate);

        await Assert.That(result is not null).IsEqualTo(expectedValid);
        if (expectedValid)
        {
            await Assert.That(result).IsEqualTo(candidate);
        }
    }

    [Test]
    public async Task GetProjectsDir_EnvOverride_TakesPrecedenceOverRealEnvironment()
    {
        var overrideDir = Path.Combine(Path.GetTempPath(), "claude-config-override-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = overrideDir };

        var result = SessionPaths.GetProjectsDir(env);

        await Assert.That(result).IsEqualTo(Path.Combine(overrideDir, "projects"));
    }
}
