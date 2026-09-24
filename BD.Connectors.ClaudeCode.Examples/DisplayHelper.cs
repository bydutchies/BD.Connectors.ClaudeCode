using BD.Connectors.ClaudeCode.Messages;

namespace BD.Connectors.ClaudeCode.Examples;

// Shared message-printing helper used across examples (PY examples each define their own
// display_message(); collapsed into one place here since C# has no per-file top-level scripts).
internal static class DisplayHelper
{
    public static void Display(Message message)
    {
        switch (message)
        {
            case UserMessage { BlockContent: { } blocks }:
                foreach (var block in blocks)
                {
                    if (block is TextBlock text)
                    {
                        Console.WriteLine($"User: {text.Text}");
                    }
                }

                break;

            case AssistantMessage assistant:
                foreach (var block in assistant.Content)
                {
                    switch (block)
                    {
                        case TextBlock text:
                            Console.WriteLine($"Claude: {text.Text}");
                            break;
                        case ToolUseBlock toolUse:
                            Console.WriteLine($"Using tool: {toolUse.Name}");
                            break;
                    }
                }

                break;

            case ResultMessage result:
                Console.WriteLine("Result ended");
                if (result.TotalCostUsd is > 0)
                {
                    Console.WriteLine($"Cost: ${result.TotalCostUsd:F4}");
                }

                break;
        }
    }
}
