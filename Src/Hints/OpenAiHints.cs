namespace Gateway.Shared.ChatTransit.Hints;

/// <summary>
/// Keys for OpenAI-specific metadata stored in <see cref="TransitRequest.Hints"/>
/// (request-level) or on <see cref="Microsoft.Extensions.AI.AIContent.AdditionalProperties"/>
/// (per-block). Values are picked up by the OpenAI Chat / Responses encoders and
/// ignored elsewhere.
/// </summary>
public static class OpenAiHints
{
    // ── Request-level (TransitRequest.Hints) ──────────────────────────────────

    /// <summary>
    /// <c>response_format</c> object (e.g. <c>{type:"json_schema", json_schema:{...}}</c>
    /// or <c>{type:"json_object"}</c>).
    /// Value: <c>JsonElement</c>.
    /// </summary>
    public const string ResponseFormat = "openai.response_format";

    /// <summary>
    /// <c>stream_options.include_usage</c> flag — when true and the request is
    /// streaming, OpenAI emits a final usage chunk.
    /// Value: <c>bool</c>.
    /// </summary>
    public const string StreamIncludeUsage = "openai.stream_include_usage";

    /// <summary>
    /// <c>parallel_tool_calls</c> flag. Value: <c>bool</c>.
    /// </summary>
    public const string ParallelToolCalls = "openai.parallel_tool_calls";

    /// <summary>
    /// <c>service_tier</c> string (e.g. <c>"auto"</c>, <c>"flex"</c>, <c>"default"</c>, <c>"scale"</c>).
    /// </summary>
    public const string ServiceTier = "openai.service_tier";

    /// <summary>
    /// <c>tool_choice</c> as returned by OpenAI clients — either a string
    /// (<c>"auto"</c>, <c>"required"</c>, <c>"none"</c>) or an object
    /// (<c>{type:"function", function:{name:"..."}}</c>).
    /// Value: <c>JsonElement</c>.
    /// Carried: OpenAI → OpenAI only. For cross-protocol routing the canonical
    /// <see cref="Microsoft.Extensions.AI.ChatOptions.ToolMode"/> is used.
    /// </summary>
    public const string ToolChoice = "openai.tool_choice";

    /// <summary>
    /// <c>logit_bias</c> object. Value: <c>JsonElement</c>.
    /// </summary>
    public const string LogitBias = "openai.logit_bias";

    /// <summary>
    /// <c>logprobs</c> flag. Value: <c>bool</c>.
    /// </summary>
    public const string Logprobs = "openai.logprobs";

    /// <summary>
    /// <c>top_logprobs</c> integer. Value: <c>int</c>.
    /// </summary>
    public const string TopLogprobs = "openai.top_logprobs";

    /// <summary>
    /// <c>n</c> — number of candidates. Value: <c>int</c>.
    /// </summary>
    public const string CandidateCount = "openai.n";

    /// <summary>
    /// <c>user</c> string for abuse tracking. Value: <c>string</c>.
    /// </summary>
    public const string User = "openai.user";

    /// <summary>
    /// <c>prompt_cache_key</c> for cross-request caching.
    /// Value: <c>string</c>.
    /// </summary>
    public const string PromptCacheKey = "openai.prompt_cache_key";

    /// <summary>
    /// <c>safety_identifier</c>. Value: <c>string</c>.
    /// </summary>
    public const string SafetyIdentifier = "openai.safety_identifier";

    /// <summary>
    /// <c>reasoning</c> object on Responses API requests
    /// (<c>{effort:"low|medium|high", summary:"auto|concise|detailed"}</c>).
    /// Value: <c>JsonElement</c>.
    /// </summary>
    public const string Reasoning = "openai.reasoning";

    /// <summary>
    /// Chat Completions top-level <c>reasoning_effort</c> (<c>"minimal"</c> /
    /// <c>"low"</c> / <c>"medium"</c> / <c>"high"</c>) — accepted by the o-series
    /// and gpt-5 family. Captured separately from <see cref="Reasoning"/> so we
    /// can also fold the effort into Anthropic <c>thinking.budget_tokens</c>
    /// and Gemini <c>thinkingConfig.thinkingLevel</c> on cross-protocol routes.
    /// Value: <c>string</c>.
    /// </summary>
    public const string ReasoningEffort = "openai.reasoning_effort";

    /// <summary>
    /// Responses API <c>instructions</c> field (system prompt in Responses shape).
    /// Carried: OpenAI Responses → OpenAI Responses.
    /// Value: <c>string</c>.
    /// </summary>
    public const string ResponsesInstructions = "openai.responses.instructions";

    /// <summary>
    /// Responses API <c>previous_response_id</c> field. Value: <c>string</c>.
    /// </summary>
    public const string PreviousResponseId = "openai.responses.previous_response_id";

    /// <summary>
    /// Responses API <c>store</c> flag. Value: <c>bool</c>.
    /// </summary>
    public const string ResponsesStore = "openai.responses.store";

    /// <summary>
    /// Responses API <c>include</c> array (e.g. <c>["file_search_call.results", "reasoning.encrypted_content"]</c>).
    /// Value: <c>JsonElement</c> (array).
    /// </summary>
    public const string ResponsesInclude = "openai.responses.include";

    /// <summary>
    /// Responses API <c>truncation</c> field. Value: <c>string</c> ("auto" or "disabled").
    /// </summary>
    public const string ResponsesTruncation = "openai.responses.truncation";

    /// <summary>
    /// Raw passthrough container for non-function tools on the Responses API
    /// (<c>file_search</c>, <c>web_search</c>, <c>web_search_preview</c>,
    /// <c>computer_use_preview</c>, <c>code_interpreter</c>, <c>image_generation</c>,
    /// <c>mcp</c>, <c>shell</c>, <c>apply_patch</c>, <c>custom</c>).
    /// Value: <c>JsonElement</c> (array of tool objects, untouched).
    /// </summary>
    public const string ResponsesBuiltinTools = "openai.responses.builtin_tools";

    /// <summary>
    /// Raw passthrough of Responses API input items the decoder couldn't fully
    /// project into MEAI ChatMessages — server-side tool calls/outputs
    /// (file_search_call, computer_call, web_search_call, image_generation_call,
    /// code_interpreter_call, mcp_call, …) and their outputs. The outbound encoder
    /// re-emits these verbatim in their original positions.
    /// Value: <c>List&lt;JsonElement&gt;</c>.
    /// </summary>
    public const string ResponsesPassthroughItems = "openai.responses.passthrough_items";

    // ── Per-content (AdditionalProperties on AIContent) ───────────────────────

    /// <summary>
    /// On a thinking <see cref="Microsoft.Extensions.AI.TextContent"/>: the
    /// encrypted reasoning blob (Responses API "reasoning.encrypted_content" /
    /// Chat Completions o1/o3 "reasoning_content").
    /// Value: <c>string</c>.
    /// <para>Also available as <see cref="Mapping.ThinkingMapper.OpenAiEncryptedContent"/>.</para>
    /// </summary>
    public const string ReasoningEncryptedContent = "openai.reasoning.encrypted_content";
}
