using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Permissions;

public abstract record PermissionResult;

public sealed record PermissionResultAllow(
    JsonObject? UpdatedInput = null,
    IReadOnlyList<PermissionUpdate>? UpdatedPermissions = null) : PermissionResult;

public sealed record PermissionResultDeny(string Message = "", bool Interrupt = false) : PermissionResult;
