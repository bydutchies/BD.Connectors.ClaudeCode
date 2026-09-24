using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;

namespace BD.Connectors.ClaudeCode.Messages;

public static class MessageOriginExtensions
{
    public static string? GetOriginKind(this JsonObject? origin) => JsonHelpers.GetString(origin, "kind");
}
