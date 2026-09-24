using System.Text.Json.Nodes;

namespace BD.Connectors.ClaudeCode.Mcp;

// PY's ToolAnnotations dual-spelling shim over mcp.types.ToolAnnotations has no C# equivalent to
// port: that shim exists only because two installed majors of the Python `mcp` package spell the
// same hints differently. This SDK has no such dependency (it has its own dispatcher), so
// ToolAnnotations is a plain record with the canonical (camelCase-on-the-wire) hint names;
// MaxResultSizeChars is a Claude Code extension carried in _meta, not an MCP hint.
public sealed record ToolAnnotations(
    string? Title = null,
    bool? ReadOnlyHint = null,
    bool? DestructiveHint = null,
    bool? IdempotentHint = null,
    bool? OpenWorldHint = null,
    int? MaxResultSizeChars = null);

// PY's tool handlers return a raw {"content": [...], "is_error": ...} dict, converted item by item.
// Content is a closed set here instead: every McpContent subtype is already one of the
// supported wire shapes, so there is no "unsupported content type" case to skip + warn about --
// only McpEmbeddedResourceContent with a null Text (PY's "binary embedded resource") is dropped.
public abstract record McpContent;

public sealed record McpTextContent(string Text) : McpContent;

public sealed record McpImageContent(string Data, string MimeType) : McpContent;

public sealed record McpResourceLinkContent(string? Name = null, string? Uri = null, string? Description = null) : McpContent;

// Text-only: PY drops a binary embedded resource with a warning rather than converting it, so there
// is nothing this type needs to represent besides the text case.
public sealed record McpEmbeddedResourceContent(string? Text = null) : McpContent;

public sealed record McpToolResult(IReadOnlyList<McpContent> Content, bool IsError = false);

// PY's SdkMcpTool + tool() decorator collapsed into one record: C# has no decorator syntax, so
// SdkMcp.Tool(...) below builds this directly instead of wrapping a handler.
public sealed record SdkMcpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<McpToolResult>> Handler,
    ToolAnnotations? Annotations = null);
