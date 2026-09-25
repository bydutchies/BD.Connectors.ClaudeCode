using System.Text.Json.Serialization;
using BD.Connectors.ClaudeCode.Messages;
using BD.Connectors.ClaudeCode.Options;

namespace BD.Connectors.ClaudeCode.Examples;

// Not a PY port -- OutputFormats/GetStructuredOutput<T> are CS-only convenience additions (see
// README's "Structured output" section), so this example doesn't have a PY equivalent to mirror.
internal static partial class StructuredOutputExample
{
    public sealed record MovieRecommendation(string Title, int Year, string Reason);

    [JsonSerializable(typeof(MovieRecommendation))]
    private sealed partial class MovieJsonContext : JsonSerializerContext;

    public static async Task RunAsync()
    {
        Console.WriteLine("=== Structured Output Example ===");

        var options = new ClaudeAgentOptions
        {
            OutputFormat = OutputFormats.JsonSchema(MovieJsonContext.Default.MovieRecommendation),
            // The CLI delivers structured output through a StructuredOutput tool call, which costs a turn
            // of its own (plus one per retry if the output fails schema validation).
            MaxTurns = 3,
        };

        await foreach (var message in ClaudeAgent.QueryAsync("Recommend one classic sci-fi movie.", options))
        {
            if (message is ResultMessage result)
            {
                if (result.StructuredOutput is null)
                {
                    Console.WriteLine($"No structured output (subtype: {result.Subtype}, turns: {result.NumTurns}, stop reason: {result.StopReason ?? "n/a"}).");
                    continue;
                }

                var movie = result.GetStructuredOutput(MovieJsonContext.Default.MovieRecommendation);
                Console.WriteLine($"Title:  {movie.Title} ({movie.Year})");
                Console.WriteLine($"Reason: {movie.Reason}");
            }
        }

        Console.WriteLine();
    }
}
