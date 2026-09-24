using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public record SystemMessage(string Subtype, JsonObject Data) : Message;
