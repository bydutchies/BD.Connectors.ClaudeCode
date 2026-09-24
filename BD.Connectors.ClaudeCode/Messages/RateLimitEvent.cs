using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Messages;

public sealed record RateLimitInfo(
    string Status,
    long? ResetsAt,
    string? RateLimitType,
    double? Utilization,
    string? OverageStatus,
    long? OverageResetsAt,
    string? OverageDisabledReason,
    JsonObject Raw);

public sealed record RateLimitEvent(RateLimitInfo RateLimitInfo, string Uuid, string SessionId) : Message;
