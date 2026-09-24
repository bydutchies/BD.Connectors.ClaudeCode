using BD.Connectors.ClaudeCode.Errors;
using BD.Connectors.ClaudeCode.Transport;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class CliLocatorTests
{
    [Test]
    public async Task FindCli_CliPathOverride_IsUsedDirectly_EvenIfNoFileExists()
    {
        var result = CliLocator.FindCli("/custom/claude", isWindows: false, pathVar: null, pathExt: null, homeDir: "/home/user", fileExists: static _ => false);

        await Assert.That(result).IsEqualTo("/custom/claude");
    }

    [Test]
    public async Task FindCli_Unix_FindsOnPath()
    {
        var result = CliLocator.FindCli(
            null,
            isWindows: false,
            pathVar: "/usr/bin:/opt/claude/bin",
            pathExt: null,
            homeDir: "/home/user",
            fileExists: p => p == "/opt/claude/bin/claude");

        await Assert.That(result).IsEqualTo("/opt/claude/bin/claude");
    }

    [Test]
    public async Task FindCli_Windows_PrefersNativeExeOnPathOverCmdShim()
    {
        // "claude" resolves to claude.cmd first via PATHEXT order in one dir, but a later dir has a
        // native claude.exe reachable via the explicit "claude.exe" probe.
        var result = CliLocator.FindCli(
            null,
            isWindows: true,
            pathVar: @"C:\npm;C:\native",
            pathExt: ".exe;.cmd",
            homeDir: @"C:\Users\test",
            fileExists: p => p is @"C:\npm\claude.cmd" or @"C:\native\claude.exe");

        await Assert.That(result).IsEqualTo(@"C:\native\claude.exe");
    }

    [Test]
    public async Task FindCli_Windows_NoNativeExeAnywhere_ReturnsShimAsLastResort()
    {
        var result = CliLocator.FindCli(
            null,
            isWindows: true,
            pathVar: @"C:\npm",
            pathExt: ".exe;.cmd",
            homeDir: @"C:\Users\test",
            fileExists: p => p == @"C:\npm\claude.cmd");

        await Assert.That(result).IsEqualTo(@"C:\npm\claude.cmd");
    }

    [Test]
    public async Task FindCli_Windows_FallsBackToFixedLocalBinLocation()
    {
        var result = CliLocator.FindCli(
            null,
            isWindows: true,
            pathVar: null,
            pathExt: null,
            homeDir: @"C:\Users\test",
            fileExists: p => p == @"C:\Users\test\.local\bin\claude.exe");

        await Assert.That(result).IsEqualTo(@"C:\Users\test\.local\bin\claude.exe");
    }

    [Test]
    public async Task FindCli_Unix_FallsBackThroughFixedLocations_InOrder()
    {
        var result = CliLocator.FindCli(
            null,
            isWindows: false,
            pathVar: null,
            pathExt: null,
            homeDir: "/home/user",
            fileExists: p => p == "/home/user/.local/bin/claude");

        await Assert.That(result).IsEqualTo("/home/user/.local/bin/claude");
    }

    [Test]
    public async Task FindCli_NothingFound_ThrowsCliNotFoundException_UnixMessage()
    {
        var action = () => CliLocator.FindCli(null, isWindows: false, pathVar: null, pathExt: null, homeDir: "/home/user", fileExists: static _ => false);

        await Assert.That(action).Throws<CliNotFoundException>();
    }

    [Test]
    public async Task FindCli_NothingFound_ThrowsCliNotFoundException_WindowsMessage()
    {
        var action = () => CliLocator.FindCli(null, isWindows: true, pathVar: null, pathExt: null, homeDir: @"C:\Users\test", fileExists: static _ => false);

        var exception = await Assert.That(action).Throws<CliNotFoundException>();
        await Assert.That(exception!.Message).Contains("claude.cmd shim");
    }

    [Test]
    [Arguments("claude.exe", true)]
    [Arguments("claude.COM", true)]
    [Arguments(@"C:\bin\claude.exe", true)]
    [Arguments("claude.cmd", false)]
    [Arguments("claude", false)]
    [Arguments("claude.exe.cmd", false)]
    public async Task IsWindowsNativeExe_ChecksFinalComponentExtension(string path, bool expected)
    {
        await Assert.That(CliLocator.IsWindowsNativeExe(path)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("claude.cmd", true)]
    [Arguments("claude.bat", true)]
    [Arguments("CLAUDE.CMD", true)]
    [Arguments("claude.cmd.", true)]
    [Arguments("claude.cmd ", true)]
    [Arguments(@"claude.cmd\subdir\claude.exe", true)]
    [Arguments(@"C:\dir\claude:evil.cmd", true)]
    [Arguments("claude.exe", false)]
    [Arguments("claude", false)]
    public async Task IsWindowsBatchCli_OnWindows_DetectsBatchExtensionInAnyComponent(string path, bool expected)
    {
        await Assert.That(CliLocator.IsWindowsBatchCli(path, isWindows: true)).IsEqualTo(expected);
    }

    [Test]
    public async Task IsWindowsBatchCli_OffWindows_AlwaysFalse()
    {
        await Assert.That(CliLocator.IsWindowsBatchCli("claude.cmd", isWindows: false)).IsFalse();
    }

    [Test]
    public async Task RejectWindowsBatchCli_OnWindows_Batch_Throws()
    {
        var action = () => CliLocator.RejectWindowsBatchCli("claude.cmd", isWindows: true);

        await Assert.That(action).Throws<CliConnectionException>();
    }

    [Test]
    public async Task RejectWindowsBatchCli_OnWindows_NativeExe_DoesNotThrow()
    {
        CliLocator.RejectWindowsBatchCli("claude.exe", isWindows: true);
    }

    [Test]
    public async Task RejectWindowsBatchCli_OffWindows_NeverThrows()
    {
        CliLocator.RejectWindowsBatchCli("claude.cmd", isWindows: false);
    }

    [Test]
    public async Task RejectWindowsCmdMetacharacters_OffWindows_NeverThrows()
    {
        CliLocator.RejectWindowsCmdMetacharacters("resume", "session&title", isWindows: false);
    }

    [Test]
    public async Task RejectWindowsCmdMetacharacters_OnWindows_CleanValue_DoesNotThrow()
    {
        CliLocator.RejectWindowsCmdMetacharacters("resume", "session-title-123", isWindows: true);
    }

    [Test]
    [Arguments("session&title")]
    [Arguments("session|title")]
    [Arguments("session\"title")]
    [Arguments("session\rtitle")]
    [Arguments("session\ntitle")]
    public async Task RejectWindowsCmdMetacharacters_OnWindows_UnsafeValue_Throws(string value)
    {
        var action = () => CliLocator.RejectWindowsCmdMetacharacters("resume", value, isWindows: true);

        var exception = await Assert.That(action).Throws<ArgumentException>();
        await Assert.That(exception!.Message).Contains("resume value");
    }

    [Test]
    [Arguments("hello", "'hello'")]
    [Arguments("it's", "\"it's\"")]
    [Arguments("back\\slash", "'back\\\\slash'")]
    [Arguments("new\nline", "'new\\nline'")]
    public async Task PyRepr_MatchesPythonReprForCommonCases(string value, string expected)
    {
        await Assert.That(CliLocator.PyRepr(value)).IsEqualTo(expected);
    }
}
