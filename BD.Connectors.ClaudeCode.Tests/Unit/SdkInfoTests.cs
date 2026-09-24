namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class SdkInfoTests
{
    [Test]
    public async Task Version_MatchesInitialPackageVersion()
    {
        var version = SdkInfo.Version;

        await Assert.That(version).IsEqualTo("0.1.0");
    }
}
