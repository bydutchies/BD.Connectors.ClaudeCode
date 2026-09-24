using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// Pure version-parsing/comparison functions behind ClaudeCli.GetVersionAsync/GetUpdateStatusAsync
// (F8.3). Golden vectors ported from CS ClaudeCliMetadataReaderTests.
[Property("TestKind", "Unit")]
public class ClaudeCliMetadataReaderTests
{
    [Test]
    public async Task ParseInstalledVersion_ReturnsFirstTokenForClaudeCodeOutput()
    {
        var parsed = ClaudeCliMetadataReader.ParseInstalledVersion("2.0.75 (Claude Code)");

        await Assert.That(parsed).IsEqualTo("2.0.75");
    }

    [Test]
    public async Task ParseInstalledVersion_EmptyOutput_Throws()
    {
        await Assert.That(() => ClaudeCliMetadataReader.ParseInstalledVersion("   ")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ParseLatestPublishedVersion_PicksHighestGitTag()
    {
        var gitOutput = "deadbeef\trefs/tags/v2.0.74\nfeedface\trefs/tags/v2.0.75-beta.1\ncafebabe\trefs/tags/v2.0.75";

        var parsed = ClaudeCliMetadataReader.ParseLatestPublishedVersion(gitOutput);

        await Assert.That(parsed).IsEqualTo("2.0.75");
    }

    [Test]
    public async Task ParseLatestPublishedVersion_UsesNumericSemVerComparisonForPrereleaseIdentifiers()
    {
        var gitOutput = "deadbeef\trefs/tags/v2.0.75-beta.2\nfeedface\trefs/tags/v2.0.75-beta.10";

        var parsed = ClaudeCliMetadataReader.ParseLatestPublishedVersion(gitOutput);

        await Assert.That(parsed).IsEqualTo("2.0.75-beta.10");
    }

    [Test]
    public async Task ParseLatestPublishedVersion_EmptyOutput_ReturnsNull()
    {
        var parsed = ClaudeCliMetadataReader.ParseLatestPublishedVersion(string.Empty);

        await Assert.That(parsed).IsNull();
    }

    [Test]
    public async Task ParseLatestPublishedVersion_NoMatchingTags_ReturnsNull()
    {
        var parsed = ClaudeCliMetadataReader.ParseLatestPublishedVersion("deadbeef\trefs/heads/main\n");

        await Assert.That(parsed).IsNull();
    }

    [Test]
    public async Task IsNewerVersion_TreatsStableAsNotOlderThanMatchingPrerelease()
    {
        var isNewer = ClaudeCliMetadataReader.IsNewerVersion("2.0.75-beta.1", "2.0.75");

        await Assert.That(isNewer).IsFalse();
    }

    [Test]
    public async Task IsNewerVersion_UsesNumericSemVerComparisonForPrereleaseIdentifiers()
    {
        var isNewer = ClaudeCliMetadataReader.IsNewerVersion("2.0.75-beta.10", "2.0.75-beta.2");

        await Assert.That(isNewer).IsTrue();
    }

    [Test]
    public async Task IsNewerVersion_HigherPatch_IsNewer()
    {
        await Assert.That(ClaudeCliMetadataReader.IsNewerVersion("2.0.76", "2.0.75")).IsTrue();
    }

    [Test]
    public async Task IsNewerVersion_UnparseableInput_ReturnsFalse()
    {
        await Assert.That(ClaudeCliMetadataReader.IsNewerVersion("not-a-version", "2.0.75")).IsFalse();
    }

    [Test]
    public async Task TryParseSemanticVersion_RejectsInvalidText()
    {
        var parsed = ClaudeCliMetadataReader.TryParseSemanticVersion("not-a-version", out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParseSemanticVersion_ParsesMajorMinorPatchAndPreRelease()
    {
        var parsed = ClaudeCliMetadataReader.TryParseSemanticVersion("2.0.75-beta.1+buildmeta", out var version);

        await Assert.That(parsed).IsTrue();
        await Assert.That(version.Major).IsEqualTo(2);
        await Assert.That(version.Minor).IsEqualTo(0);
        await Assert.That(version.Patch).IsEqualTo(75);
        await Assert.That(version.PreRelease).IsEqualTo("beta.1");
    }

    [Test]
    public async Task TryParseSemanticVersion_MajorOnly_DefaultsMinorAndPatchToZero()
    {
        var parsed = ClaudeCliMetadataReader.TryParseSemanticVersion("3", out var version);

        await Assert.That(parsed).IsTrue();
        await Assert.That(version.Major).IsEqualTo(3);
        await Assert.That(version.Minor).IsEqualTo(0);
        await Assert.That(version.Patch).IsEqualTo(0);
    }
}
