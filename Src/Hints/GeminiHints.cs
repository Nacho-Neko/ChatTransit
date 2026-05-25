namespace Gateway.Shared.ChatTransit.Hints;

/// <summary>
/// Keys for Gemini-specific metadata stored in <see cref="TransitRequest.Hints"/>
/// (request-level) or on <see cref="Microsoft.Extensions.AI.AIContent.AdditionalProperties"/>
/// (per-block). Values are picked up by <c>GeminiOutboundEncoder</c> and ignored
/// by other encoders.
/// </summary>
public static class GeminiHints
{
    // ── Per-content ───────────────────────────────────────────────────────────

    /// <summary>
    /// <c>thoughtSignature</c> — the opaque blob Gemini 3 returns on thought parts.
    /// MUST be returned inside its original Part for thinking continuity (per
    /// official function-calling docs). Stored on the thinking content via
    /// <see cref="Mapping.ThinkingMapper.GeminiThoughtSignature"/>.
    /// </summary>
    public const string ThoughtSignature = "gemini.thought_signature";

    /// <summary>
    /// <c>videoMetadata</c> object on video parts (start/end offsets, fps).
    /// Value: <c>JsonElement</c>.
    /// </summary>
    public const string VideoMetadata = "gemini.video_metadata";

    // ── Request-level (TransitRequest.Hints) ──────────────────────────────────

    /// <summary>
    /// <c>safetySettings</c> array from the original request.
    /// Value: <c>JsonElement</c> (array).
    /// Carried: Gemini → Gemini.
    /// </summary>
    public const string SafetySettings = "gemini.safety_settings";

    /// <summary>
    /// <c>cachedContent</c> resource name string.
    /// Value: <c>string</c>. Carried: Gemini → Gemini.
    /// </summary>
    public const string CachedContent = "gemini.cached_content";

    /// <summary>
    /// <c>generationConfig.responseMimeType</c>.
    /// Value: <c>string</c> (e.g. <c>"application/json"</c>).
    /// </summary>
    public const string ResponseMimeType = "gemini.response_mime_type";

    /// <summary>
    /// <c>generationConfig.responseSchema</c> JSON schema object.
    /// Value: <c>JsonElement</c>.
    /// </summary>
    public const string ResponseSchema = "gemini.response_schema";

    /// <summary>
    /// <c>generationConfig.responseJsonSchema</c> alternative JSON Schema field.
    /// Value: <c>JsonElement</c>.
    /// </summary>
    public const string ResponseJsonSchema = "gemini.response_json_schema";

    /// <summary>
    /// <c>generationConfig.responseModalities</c> array (e.g. <c>["TEXT", "IMAGE"]</c>).
    /// Value: <c>JsonElement</c> (array of strings).
    /// </summary>
    public const string ResponseModalities = "gemini.response_modalities";

    /// <summary>
    /// <c>toolConfig.functionCallingConfig</c> object.
    /// Value: <c>JsonElement</c>.
    /// Carried: Gemini → Gemini only. For cross-protocol routing the canonical
    /// <see cref="Microsoft.Extensions.AI.ChatOptions.ToolMode"/> is used.
    /// </summary>
    public const string ToolConfig = "gemini.tool_config";

    /// <summary>
    /// Raw passthrough for non-function tool blocks inside <c>tools[]</c> — i.e.
    /// the built-in retrievers <c>googleSearch</c>, <c>googleSearchRetrieval</c>,
    /// <c>urlContext</c>, <c>codeExecution</c>, <c>retrieval</c>,
    /// <c>enterpriseWebSearch</c>, and any other non-<c>functionDeclarations</c>
    /// entry. Stored verbatim and re-emitted on Gemini → Gemini round-trip.
    /// Value: <c>List&lt;JsonElement&gt;</c>.
    /// </summary>
    public const string BuiltinTools = "gemini.builtin_tools";

    /// <summary>
    /// <c>generationConfig.thinkingConfig.thinkingBudget</c> in tokens.
    /// Value: <c>int</c>. -1 means dynamic.
    /// </summary>
    public const string ThinkingBudget = "gemini.thinking_budget";

    /// <summary>
    /// <c>generationConfig.thinkingConfig.thinkingLevel</c> (Gemini 3 only:
    /// "low" / "medium" / "high" / "dynamic").
    /// Value: <c>string</c>.
    /// </summary>
    public const string ThinkingLevel = "gemini.thinking_level";

    /// <summary>
    /// <c>generationConfig.thinkingConfig.includeThoughts</c> flag.
    /// Value: <c>bool</c>.
    /// </summary>
    public const string IncludeThoughts = "gemini.include_thoughts";

    /// <summary>
    /// <c>generationConfig.candidateCount</c>. Value: <c>int</c>.
    /// </summary>
    public const string CandidateCount = "gemini.candidate_count";

    /// <summary>
    /// <c>generationConfig.responseLogprobs</c>. Value: <c>bool</c>.
    /// </summary>
    public const string ResponseLogprobs = "gemini.response_logprobs";

    /// <summary>
    /// <c>generationConfig.logprobs</c> integer. Value: <c>int</c>.
    /// </summary>
    public const string Logprobs = "gemini.logprobs";

    /// <summary>
    /// <c>generationConfig.audioTimestamp</c>. Value: <c>bool</c>.
    /// </summary>
    public const string AudioTimestamp = "gemini.audio_timestamp";

    /// <summary>
    /// <c>generationConfig.mediaResolution</c> string ("MEDIA_RESOLUTION_LOW" etc.).
    /// </summary>
    public const string MediaResolution = "gemini.media_resolution";

    /// <summary>
    /// <c>generationConfig.speechConfig</c> object for TTS-capable models.
    /// Value: <c>JsonElement</c>.
    /// </summary>
    public const string SpeechConfig = "gemini.speech_config";

    /// <summary>
    /// <c>labels</c> map for billing / abuse tracking. Value: <c>JsonElement</c>.
    /// </summary>
    public const string Labels = "gemini.labels";

    /// <summary>
    /// Whether the original request was for the Vertex AI flavour (vs. AI Studio).
    /// Mostly used for transport routing decisions. Value: <c>bool</c>.
    /// </summary>
    public const string IsVertex = "gemini.is_vertex";
}
