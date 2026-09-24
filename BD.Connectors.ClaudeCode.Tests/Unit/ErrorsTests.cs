using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Errors;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

[Property("TestKind", "Unit")]
public class ErrorsTests
{
    [Test]
    public async Task BaseError_MessageRoundTrips()
    {
        var error = new ClaudeSdkException("Something went wrong");

        await Assert.That(error.Message).IsEqualTo("Something went wrong");
        await Assert.That(error).IsAssignableTo<Exception>();
    }

    [Test]
    public async Task CliNotFoundException_IsClaudeSdkException()
    {
        var error = new CliNotFoundException("Claude Code not found");

        await Assert.That(error).IsAssignableTo<ClaudeSdkException>();
        await Assert.That(error.Message).Contains("Claude Code not found");
    }

    [Test]
    public async Task CliNotFoundException_AppendsCliPath()
    {
        var error = new CliNotFoundException(cliPath: "/usr/local/bin/claude");

        await Assert.That(error.Message).IsEqualTo("Claude Code not found: /usr/local/bin/claude");
    }

    [Test]
    public async Task CliConnectionException_IsClaudeSdkException()
    {
        var error = new CliConnectionException("Failed to connect to CLI");

        await Assert.That(error).IsAssignableTo<ClaudeSdkException>();
        await Assert.That(error.Message).Contains("Failed to connect to CLI");
    }

    [Test]
    public async Task ProcessException_FormatsExitCodeAndStderr()
    {
        var error = new ProcessException("Process failed", exitCode: 1, stderr: "Command not found");

        await Assert.That(error.ExitCode).IsEqualTo(1);
        await Assert.That(error.Stderr).IsEqualTo("Command not found");
        await Assert.That(error.Message).Contains("Process failed");
        await Assert.That(error.Message).Contains("exit code: 1");
        await Assert.That(error.Message).Contains("Command not found");
    }

    [Test]
    public async Task ResultException_CarriesPayload()
    {
        var data = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = true,
            ["errors"] = new JsonArray(),
            ["result"] = "API Error: Stream idle timeout - no chunks received",
            ["api_error_status"] = null,
            ["terminal_reason"] = "api_error",
            ["session_id"] = "s-1",
        };

        var error = new ResultException("Claude Code returned an error result: x", data, 1);

        await Assert.That(error).IsAssignableTo<ProcessException>();
        await Assert.That(error.ExitCode).IsEqualTo(1);
        await Assert.That(error.Payload).IsEqualTo(data);
        await Assert.That(error.Subtype).IsEqualTo("success");
        await Assert.That(error.Errors).IsEmpty();
        await Assert.That(error.Result).IsEqualTo("API Error: Stream idle timeout - no chunks received");
        await Assert.That(error.ApiErrorStatus).IsNull();
        await Assert.That(error.TerminalReason).IsEqualTo("api_error");
        await Assert.That(error.SessionId).IsEqualTo("s-1");
        await Assert.That(error.Message).Contains("exit code: 1");
    }

    [Test]
    public async Task ResultException_TolerateMissingOrMalformedFields()
    {
        var data = new JsonObject { ["errors"] = 42, ["api_error_status"] = "500" };
        var error = new ResultException("boom", data);

        await Assert.That(error.Subtype).IsNull();
        await Assert.That(error.Errors).IsEmpty();
        await Assert.That(error.Result).IsNull();
        await Assert.That(error.ApiErrorStatus).IsNull();
        await Assert.That(error.TerminalReason).IsNull();
        await Assert.That(error.SessionId).IsNull();
        await Assert.That(error.ExitCode).IsNull();
        await Assert.That(new ResultException("boom").Payload).IsEmpty();
    }

    [Test]
    public async Task ResultException_NormalizesErrorsLikeTheMessageText()
    {
        var bareString = new ResultException("m", new JsonObject { ["errors"] = "boom" });
        await Assert.That(string.Join(",", bareString.Errors)).IsEqualTo("boom");

        var mixedArray = new ResultException("m", new JsonObject { ["errors"] = new JsonArray(" ", "x ", 3) });
        await Assert.That(string.Join(",", mixedArray.Errors)).IsEqualTo("x");
    }

    [Test]
    public async Task CliJsonDecodeException_FormatsMessage()
    {
        var original = new FormatException("invalid");
        var error = new CliJsonDecodeException("{invalid json}", original);

        await Assert.That(error.Line).IsEqualTo("{invalid json}");
        await Assert.That(error.OriginalError).IsEqualTo(original);
        await Assert.That(error.Message).Contains("Failed to decode JSON");
    }
}
