using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Options;
using BD.Connectors.ClaudeCode.Permissions;

namespace BD.Connectors.ClaudeCode.Examples;

// Port of PY's corresponding example.
internal static class ToolPermissionCallbackExample
{
    private static readonly string[] _dangerousBashPatterns = ["rm -rf", "sudo", "chmod 777", "dd if=", "mkfs"];

    public static async Task RunAsync()
    {
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("Tool Permission Callback Example");
        Console.WriteLine(new string('=', 60));

        var options = new ClaudeAgentOptions
        {
            CanUseTool = MyPermissionCallbackAsync,
            PermissionMode = PermissionMode.Default,
            Cwd = ".",
        };

        await using var client = new ClaudeSdkClient(options);
        await client.ConnectAsync();

        Console.WriteLine("\nSending query to Claude...");
        await client.QueryAsync(
            "Please do the following:\n"
            + "1. List the files in the current directory\n"
            + "2. Create a simple hello world file at hello.txt\n"
            + "3. Show its contents");

        await foreach (var message in client.ReceiveResponseAsync())
        {
            DisplayHelper.Display(message);
        }
    }

    // Controls tool permissions based on tool type and input, mirroring PY's my_permission_callback.
    private static Task<PermissionResult> MyPermissionCallbackAsync(string toolName, JsonObject input, ToolPermissionContext context)
    {
        Console.WriteLine($"\nTool Permission Request: {toolName}");
        Console.WriteLine($"   Input: {input.ToJsonString()}");

        // Always allow read operations.
        if (toolName is "Read" or "Glob" or "Grep")
        {
            Console.WriteLine($"   Automatically allowing {toolName} (read-only operation)");
            return Task.FromResult<PermissionResult>(new PermissionResultAllow());
        }

        // Deny write operations to system directories; redirect other writes to a safe directory.
        if (toolName is "Write" or "Edit" or "MultiEdit")
        {
            var filePath = input["file_path"]?.ToString() ?? string.Empty;
            if (filePath.StartsWith("/etc/", StringComparison.Ordinal) || filePath.StartsWith("/usr/", StringComparison.Ordinal))
            {
                Console.WriteLine($"   Denying write to system directory: {filePath}");
                return Task.FromResult<PermissionResult>(new PermissionResultDeny($"Cannot write to system directory: {filePath}"));
            }

            if (!filePath.StartsWith("/tmp/", StringComparison.Ordinal) && !filePath.StartsWith("./", StringComparison.Ordinal))
            {
                var safePath = $"./safe_output/{filePath.Split('/')[^1]}";
                Console.WriteLine($"   Redirecting write from {filePath} to {safePath}");
                var updatedInput = (JsonObject)input.DeepClone();
                updatedInput["file_path"] = safePath;
                return Task.FromResult<PermissionResult>(new PermissionResultAllow(updatedInput));
            }
        }

        // Deny dangerous bash commands, otherwise allow.
        if (toolName == "Bash")
        {
            var command = input["command"]?.ToString() ?? string.Empty;
            foreach (var dangerous in _dangerousBashPatterns)
            {
                if (command.Contains(dangerous, StringComparison.Ordinal))
                {
                    Console.WriteLine($"   Denying dangerous command: {command}");
                    return Task.FromResult<PermissionResult>(new PermissionResultDeny($"Dangerous command pattern detected: {dangerous}"));
                }
            }

            Console.WriteLine($"   Allowing bash command: {command}");
            return Task.FromResult<PermissionResult>(new PermissionResultAllow());
        }

        // For everything else, allow by default (PY prompts interactively; a non-interactive example
        // just allows and logs).
        Console.WriteLine($"   Unknown tool: {toolName} -- allowing by default");
        return Task.FromResult<PermissionResult>(new PermissionResultAllow());
    }
}
