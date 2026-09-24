namespace BD.Connectors.ClaudeCode.Internal;

internal static class WireConstants
{
    public static class MessageTypes
    {
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string System = "system";
        public const string Result = "result";
        public const string StreamEvent = "stream_event";
        public const string RateLimitEvent = "rate_limit_event";
        public const string ConversationReset = "conversation_reset";
        public const string ControlRequest = "control_request";
        public const string ControlResponse = "control_response";
        public const string ControlCancelRequest = "control_cancel_request";
        public const string TranscriptMirror = "transcript_mirror";
    }

    public static class SystemSubtypes
    {
        public const string Init = "init";
        public const string TaskStarted = "task_started";
        public const string TaskProgress = "task_progress";
        public const string TaskNotification = "task_notification";
        public const string TaskUpdated = "task_updated";
        public const string MirrorError = "mirror_error";
        public const string HookStarted = "hook_started";
        public const string HookResponse = "hook_response";
        public const string SessionStateChanged = "session_state_changed";
    }

    public static class ControlSubtypes
    {
        public const string Initialize = "initialize";
        public const string CanUseTool = "can_use_tool";
        public const string HookCallback = "hook_callback";
        public const string McpMessage = "mcp_message";
        public const string Interrupt = "interrupt";
        public const string SetPermissionMode = "set_permission_mode";
        public const string SetModel = "set_model";
        public const string RewindFiles = "rewind_files";
        public const string McpReconnect = "mcp_reconnect";
        public const string McpToggle = "mcp_toggle";
        public const string StopTask = "stop_task";
        public const string McpStatus = "mcp_status";
        public const string GetContextUsage = "get_context_usage";
        public const string Success = "success";
        public const string Error = "error";
    }

    public static class ContentTypes
    {
        public const string Text = "text";
        public const string Thinking = "thinking";
        public const string ToolUse = "tool_use";
        public const string ToolResult = "tool_result";
        public const string ServerToolUse = "server_tool_use";
        public const string AdvisorToolResult = "advisor_tool_result";
    }

    public static class Keys
    {
        public const string Type = "type";
        public const string Subtype = "subtype";
        public const string Message = "message";
        public const string Content = "content";
        public const string Role = "role";
        public const string Uuid = "uuid";
        public const string ParentToolUseId = "parent_tool_use_id";
        public const string ToolUseResult = "tool_use_result";
        public const string Origin = "origin";
        public const string Kind = "kind";
        public const string Model = "model";
        public const string Error = "error";
        public const string Usage = "usage";
        public const string Id = "id";
        public const string StopReason = "stop_reason";
        public const string SessionId = "session_id";
        public const string Name = "name";
        public const string Input = "input";
        public const string ToolUseId = "tool_use_id";
        public const string IsError = "is_error";
        public const string DurationMs = "duration_ms";
        public const string DurationApiMs = "duration_api_ms";
        public const string NumTurns = "num_turns";
        public const string TotalCostUsd = "total_cost_usd";
        public const string StructuredOutput = "structured_output";
        public const string ModelUsage = "modelUsage";
        public const string PermissionDenials = "permission_denials";
        public const string DeferredToolUse = "deferred_tool_use";
        public const string Errors = "errors";
        public const string ApiErrorStatus = "api_error_status";
        public const string TerminalReason = "terminal_reason";
        public const string Result = "result";
        public const string Event = "event";
        public const string RateLimitInfo = "rate_limit_info";
        public const string Status = "status";
        public const string ResetsAt = "resetsAt";
        public const string RateLimitType = "rateLimitType";
        public const string Utilization = "utilization";
        public const string OverageStatus = "overageStatus";
        public const string OverageResetsAt = "overageResetsAt";
        public const string OverageDisabledReason = "overageDisabledReason";
        public const string NewConversationId = "new_conversation_id";
        public const string TaskId = "task_id";
        public const string Description = "description";
        public const string TaskType = "task_type";
        public const string LastToolName = "last_tool_name";
        public const string OutputFile = "output_file";
        public const string Summary = "summary";
        public const string Patch = "patch";
        public const string Key = "key";
        public const string HookEvent = "hook_event";
        public const string HookName = "hook_name";
        public const string HookEventName = "hook_event_name";
    }

    // Argument names as passed to ArgumentList (without the leading "--"); see CliCommandBuilder (F2).
    public static class CliFlags
    {
        public const string OutputFormat = "output-format";
        public const string Verbose = "verbose";
        public const string SystemPrompt = "system-prompt";
        public const string SystemPromptFile = "system-prompt-file";
        public const string AppendSystemPrompt = "append-system-prompt";
        public const string Tools = "tools";
        public const string AllowedTools = "allowedTools";
        public const string MaxTurns = "max-turns";
        public const string MaxBudgetUsd = "max-budget-usd";
        public const string DisallowedTools = "disallowedTools";
        public const string TaskBudget = "task-budget";
        public const string Model = "model";
        public const string FallbackModel = "fallback-model";
        public const string Betas = "betas";
        public const string PermissionPromptTool = "permission-prompt-tool";
        public const string PermissionMode = "permission-mode";
        public const string Continue = "continue";
        public const string Resume = "resume";
        public const string SessionId = "session-id";
        public const string Settings = "settings";
        public const string AddDir = "add-dir";
        public const string McpConfig = "mcp-config";
        public const string IncludePartialMessages = "include-partial-messages";
        public const string IncludeHookEvents = "include-hook-events";
        public const string StrictMcpConfig = "strict-mcp-config";
        public const string ForkSession = "fork-session";
        public const string ResumeSessionAt = "resume-session-at";
        public const string ResumeDropsTurn = "resume-drops-turn";
        public const string SessionMirror = "session-mirror";
        public const string SettingSources = "setting-sources";
        public const string PluginDir = "plugin-dir";
        public const string Thinking = "thinking";
        public const string ThinkingDisplay = "thinking-display";
        public const string MaxThinkingTokens = "max-thinking-tokens";
        public const string Effort = "effort";
        public const string JsonSchema = "json-schema";
        public const string InputFormat = "input-format";
    }

    public static class EnvVars
    {
        public const string ClaudeCode = "CLAUDECODE";
        public const string ClaudeCodeEntrypoint = "CLAUDE_CODE_ENTRYPOINT";
        public const string ClaudeAgentSdkVersion = "CLAUDE_AGENT_SDK_VERSION";
        public const string ClaudeCodeEnableSdkFileCheckpointing = "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING";
        public const string Pwd = "PWD";
        public const string ClaudeAgentSdkSkipVersionCheck = "CLAUDE_AGENT_SDK_SKIP_VERSION_CHECK";
        public const string ClaudeCodeStreamCloseTimeout = "CLAUDE_CODE_STREAM_CLOSE_TIMEOUT";
        public const string ClaudeConfigDir = "CLAUDE_CONFIG_DIR";
        public const string Traceparent = "TRACEPARENT";
        public const string Tracestate = "TRACESTATE";
    }
}
