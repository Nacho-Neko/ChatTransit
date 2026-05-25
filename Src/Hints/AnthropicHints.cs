namespace Gateway.Shared.ChatTransit.Hints;

/// <summary>
/// Keys for Anthropic-specific metadata stored in <see cref="TransitRequest.Hints"/>
/// (request-level) or on <see cref="Microsoft.Extensions.AI.AIContent.AdditionalProperties"/>
/// (per-block). Values are picked up by <c>AnthropicOutboundEncoder</c> and ignored
/// by other encoders.
/// </summary>
public static class AnthropicHints
{
    // ── Per-content (AdditionalProperties on AIContent) ───────────────────────

    /// <summary>
    /// <c>cache_control</c> object attached to a specific content block.
    /// Value: <c>JsonElement</c> or object compatible with <c>{"type":"ephemeral"}</c>.
    /// Carried: Anthropic → Anthropic (passthrough only).
    /// </summary>
    public const string CacheControl = "anthropic.cache_control";

    /// <summary>
    /// <c>thinking.signature</c> — the cryptographic blob Anthropic returns on
    /// thinking blocks. MUST be sent back byte-for-byte unchanged in multi-turn
    /// conversations or Anthropic returns HTTP 400.
    /// Value: <c>string</c>.
    /// <para>Also available via <see cref="Mapping.ThinkingMapper.AnthropicSignature"/>
    /// on TextContent.AdditionalProperties.</para>
    /// </summary>
    public const string ThinkingSignature = "anthropic.thinking.signature";

    /// <summary>
    /// <c>redacted_thinking.data</c> — opaque encrypted thinking payload.
    /// Must be preserved verbatim across turns.
    /// </summary>
    public const string RedactedThinkingData = "anthropic.redacted_thinking.data";

    // ── Per-request (TransitRequest.Hints) ────────────────────────────────────

    /// <summary>
    /// Raw <c>anthropic-beta</c> header value from the incoming request.
    /// Value: <c>string</c> (comma-joined).
    /// Carried: Anthropic → Anthropic. Consumed by the HTTP transport layer
    /// (not by the body encoder) when the target is also Anthropic.
    /// </summary>
    public const string BetaHeader = "anthropic.beta_header";

    /// <summary>
    /// Full <c>tool_choice</c> object as returned by Anthropic clients
    /// (<c>{type:"auto"}</c>, <c>{type:"any"}</c>, <c>{type:"tool", name:"..."}</c>,
    /// <c>{type:"none"}</c>).
    /// Value: <c>JsonElement</c>.
    /// Carried: Anthropic → Anthropic only. For cross-protocol routing the
    /// canonical <see cref="Microsoft.Extensions.AI.ChatOptions.ToolMode"/> is used.
    /// </summary>
    public const string ToolChoice = "anthropic.tool_choice";

    /// <summary>
    /// Whether the model is operating in extended-thinking mode. Set by the
    /// inbound decoder when the request body contains <c>"thinking": {type:"enabled"}</c>
    /// or <c>{type:"adaptive"}</c>. Used by the outbound encoder to strip
    /// temperature/top_p/top_k (Anthropic rejects them in thinking mode) and to
    /// auto-inject <c>thinking.budget_tokens</c>.
    /// Value: <c>bool</c>.
    /// </summary>
    public const string IsThinkingModel = "anthropic.is_thinking_model";

    /// <summary>
    /// Full original <c>thinking</c> config object (<c>{type, budget_tokens?}</c>).
    /// Preferred over the auto-budget computation when present.
    /// Value: <c>JsonElement</c>.
    /// Carried: Anthropic → Anthropic only.
    /// </summary>
    public const string ThinkingConfig = "anthropic.thinking_config";

    /// <summary>
    /// <c>metadata</c> object — abuse-tracking labels Anthropic accepts
    /// (e.g. <c>{user_id:"..."}</c>).
    /// Value: <c>JsonElement</c>.
    /// </summary>
    public const string Metadata = "anthropic.metadata";

    /// <summary>
    /// <c>system</c> field in array-of-blocks form (when the inbound request
    /// supplied multiple blocks with cache_control attached). Stored as the
    /// raw JSON array so the outbound encoder can replay it verbatim. When
    /// present, supersedes the flattened system string.
    /// Value: <c>JsonElement</c> (array).
    /// </summary>
    public const string SystemBlocks = "anthropic.system_blocks";

    /// <summary>
    /// <c>container</c> id for sandboxed code-execution sessions.
    /// Value: <c>string</c>.
    /// </summary>
    public const string Container = "anthropic.container";

    /// <summary>
    /// <c>service_tier</c> — Anthropic priority/standard tier selection.
    /// Value: <c>string</c>.
    /// </summary>
    public const string ServiceTier = "anthropic.service_tier";
}
