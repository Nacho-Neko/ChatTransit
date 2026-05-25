namespace Gateway.Shared.ChatTransit.Mapping;

/// <summary>
/// Maps stop / finish-reason strings between the four supported protocol vocabularies
/// and the IR canonical form.
///
/// <para><b>Canonical IR values</b> (used by <see cref="StreamingChunkDto.FinishReason"/>
/// and emitted by every provider channel):</para>
/// <list type="bullet">
///   <item><c>stop</c> — natural end of turn (OpenAI <c>stop</c>, Anthropic <c>end_turn</c>, Gemini <c>STOP</c>)</item>
///   <item><c>length</c> — token cap hit (OpenAI <c>length</c>, Anthropic <c>max_tokens</c>, Gemini <c>MAX_TOKENS</c>)</item>
///   <item><c>tool_calls</c> — model requested tool execution (OpenAI <c>tool_calls</c>, Anthropic <c>tool_use</c>, Gemini <c>FUNCTION_CALL</c>)</item>
///   <item><c>stop_sequence</c> — custom stop sequence matched (Anthropic <c>stop_sequence</c>, OpenAI rolls into <c>stop</c>)</item>
///   <item><c>content_filter</c> — safety/content filter blocked output (OpenAI <c>content_filter</c>, Gemini <c>SAFETY</c>/<c>BLOCKLIST</c>/<c>PROHIBITED_CONTENT</c>/<c>SPII</c>, Anthropic <c>refusal</c>)</item>
///   <item><c>recitation</c> — Gemini-specific (training-data overlap blocked output)</item>
///   <item><c>pause_turn</c> — Anthropic-specific (extended thinking / long server-side tool turn paused)</item>
///   <item><c>malformed_function_call</c> — Gemini-specific</item>
///   <item><c>model_context_window_exceeded</c> — Anthropic-specific</item>
/// </list>
///
/// <para>Each <c>To*</c> helper takes a string in any of these vocabularies (IR
/// canonical, or any native vocabulary) and returns the target's native value.</para>
/// </summary>
public static class StopReasonMapper
{
    // ── IR canonical values (re-exported so callers don't typo) ───────────────

    public const string IrStop = "stop";
    public const string IrLength = "length";
    public const string IrToolCalls = "tool_calls";
    public const string IrStopSequence = "stop_sequence";
    public const string IrContentFilter = "content_filter";
    public const string IrRecitation = "recitation";
    public const string IrPauseTurn = "pause_turn";
    public const string IrMalformedFunctionCall = "malformed_function_call";
    public const string IrModelContextWindowExceeded = "model_context_window_exceeded";

    /// <summary>
    /// Normalises any provider-native finish-reason string into the IR canonical form.
    /// Returns <c>null</c> when the input is <c>null</c> or empty (streaming chunks
    /// often omit the field until the final delta).
    /// </summary>
    public static string? ToIr(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        return source.ToLowerInvariant() switch
        {
            // OpenAI Chat / Responses
            "stop" => IrStop,
            "length" => IrLength,
            "tool_calls" or "function_call" => IrToolCalls,
            "content_filter" => IrContentFilter,

            // Anthropic
            "end_turn" => IrStop,
            "max_tokens" => IrLength,
            "tool_use" => IrToolCalls,
            "stop_sequence" => IrStopSequence,
            "refusal" => IrContentFilter,
            "pause_turn" => IrPauseTurn,
            "model_context_window_exceeded" => IrModelContextWindowExceeded,

            // Gemini
            "function_call" or "tool_use_call" => IrToolCalls,
            "safety" or "blocklist" or "prohibited_content" or "spii" => IrContentFilter,
            "recitation" => IrRecitation,
            "malformed_function_call" => IrMalformedFunctionCall,
            "max_tokens_reached" => IrLength,
            "finish_reason_unspecified" or "other" => IrStop,

            // Already canonical / unknown — passthrough lowercase
            _ => source.ToLowerInvariant()
        };
    }

    // ── OpenAI Chat finish_reason ─────────────────────────────────────────────

    /// <summary>
    /// Maps any source (IR or native) to OpenAI Chat <c>finish_reason</c>.
    /// OpenAI accepts: <c>stop</c>, <c>length</c>, <c>tool_calls</c>, <c>content_filter</c>, <c>function_call</c> (deprecated).
    /// Provider-specific values that have no OpenAI mapping (e.g. <c>pause_turn</c>,
    /// <c>recitation</c>) fold into <c>stop</c> as the safest default — the response
    /// body still contains the upstream's stop_details for clients that care.
    /// </summary>
    public static string ToOpenAi(string? source) => ToIr(source) switch
    {
        IrLength => "length",
        IrToolCalls => "tool_calls",
        IrContentFilter => "content_filter",
        // stop_sequence/recitation/pause_turn/malformed_function_call all map to "stop"
        _ => "stop"
    };

    // ── Anthropic stop_reason ─────────────────────────────────────────────────

    /// <summary>
    /// Maps any source (IR or native) to Anthropic <c>stop_reason</c>.
    /// Anthropic accepts: <c>end_turn</c>, <c>max_tokens</c>, <c>stop_sequence</c>,
    /// <c>tool_use</c>, <c>pause_turn</c>, <c>refusal</c>, <c>model_context_window_exceeded</c>.
    /// </summary>
    public static string ToAnthropic(string? source) => ToIr(source) switch
    {
        IrLength => "max_tokens",
        IrToolCalls => "tool_use",
        IrStopSequence => "stop_sequence",
        IrContentFilter => "refusal",
        IrPauseTurn => "pause_turn",
        IrModelContextWindowExceeded => "model_context_window_exceeded",
        IrRecitation => "refusal", // closest Anthropic concept
        IrMalformedFunctionCall => "end_turn",
        _ => "end_turn"
    };

    // ── Gemini finishReason ───────────────────────────────────────────────────

    /// <summary>
    /// Maps any source (IR or native) to Gemini <c>finishReason</c>.
    /// Gemini accepts: <c>FINISH_REASON_UNSPECIFIED</c>, <c>STOP</c>, <c>MAX_TOKENS</c>,
    /// <c>SAFETY</c>, <c>RECITATION</c>, <c>PROHIBITED_CONTENT</c>, <c>BLOCKLIST</c>,
    /// <c>SPII</c>, <c>MALFORMED_FUNCTION_CALL</c>, <c>OTHER</c>.
    /// </summary>
    public static string ToGemini(string? source) => ToIr(source) switch
    {
        IrLength => "MAX_TOKENS",
        IrToolCalls => "STOP", // Gemini does NOT use FUNCTION_CALL as finishReason — its candidates carry tool parts and finishReason stays "STOP"
        IrContentFilter => "SAFETY",
        IrRecitation => "RECITATION",
        IrMalformedFunctionCall => "MALFORMED_FUNCTION_CALL",
        IrStopSequence => "STOP",
        IrPauseTurn => "STOP",
        IrModelContextWindowExceeded => "MAX_TOKENS",
        _ => "STOP"
    };

    /// <summary>
    /// Derives the OpenAI Chat <c>finish_reason</c> for the assistant message.
    /// Used by response encoders: if any <see cref="StreamingChunkDto"/> set a
    /// FinishReason it's mapped via <see cref="ToOpenAi"/>; otherwise we fall back
    /// to <c>tool_calls</c> when the response contained tool invocations,
    /// <c>stop</c> otherwise.
    /// </summary>
    public static string DeriveOpenAiFinishReason(string? upstreamFinishReason, bool hadToolCalls)
    {
        if (!string.IsNullOrEmpty(upstreamFinishReason))
            return ToOpenAi(upstreamFinishReason);
        return hadToolCalls ? "tool_calls" : "stop";
    }

    /// <summary>Same as <see cref="DeriveOpenAiFinishReason"/> but for Anthropic.</summary>
    public static string DeriveAnthropicStopReason(string? upstreamFinishReason, bool hadToolCalls)
    {
        if (!string.IsNullOrEmpty(upstreamFinishReason))
            return ToAnthropic(upstreamFinishReason);
        return hadToolCalls ? "tool_use" : "end_turn";
    }

    /// <summary>Same as <see cref="DeriveOpenAiFinishReason"/> but for Gemini.</summary>
    public static string DeriveGeminiFinishReason(string? upstreamFinishReason, bool _)
    {
        if (!string.IsNullOrEmpty(upstreamFinishReason))
            return ToGemini(upstreamFinishReason);
        // Gemini treats tool calls as STOP — the FunctionCall sits in the parts array
        return "STOP";
    }
}
