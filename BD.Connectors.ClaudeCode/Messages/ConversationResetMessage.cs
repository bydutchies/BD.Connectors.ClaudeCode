namespace BD.Connectors.ClaudeCode.Messages;

public sealed record ConversationResetMessage(string NewConversationId, string Uuid, string SessionId) : Message;
