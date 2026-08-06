using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ChatTransit.Mapping;

/// <summary>
/// Handles the different ways each protocol exposes "thinking" / "reasoning" tokens
/// — including the per-block opaque <b>signature / thought_signature / encrypted_content</b>
/// values that providers REQUIRE to be round-tripped byte-for-byte in multi-turn
/// conversations. Failing to preserve these triggers HTTP 400 errors on the next
/// request (Anthropic: "thinking blocks cannot be modified"; Gemini 3: silent loss
/// of thinking continuity).
///
/// <para>Wire mappings:</para>
/// <list type="bullet">
///   <item>OpenAI o1/o3: <c>reasoning_content</c> field (text) + <c>reasoning.encrypted_content</c></item>
///   <item>Anthropic: <c>{type:"thinking", thinking:"...", signature:"..."}</c> and <c>{type:"redacted_thinking", data:"..."}</c></item>
///   <item>Gemini: <c>{thought:true, text:"...", thoughtSignature:"..."}</c></item>
/// </list>
///
/// <para>All of these are stored on a <see cref="TextContent"/> via well-known
/// <see cref="AIContent.AdditionalProperties"/> keys, with the Text holding the
/// human-readable thinking transcript (or "[redacted]" for redacted blocks).</para>
/// </summary>
public static class ThinkingMapper
{
    // ── AdditionalProperties keys ─────────────────────────────────────────────

    /// <summary>Marker key indicating this TextContent represents a thinking block.</summary>
    public const string ThinkingMarker = "transit.thinking";

    /// <summary>Distinguishes <c>"thinking"</c> from <c>"redacted_thinking"</c> blocks.</summary>
    public const string ThinkingKind = "transit.thinking.kind";

    /// <summary>Anthropic <c>signature</c> field (opaque cryptographic blob).</summary>
    public const string AnthropicSignature = "transit.thinking.anthropic.signature";

    /// <summary>Anthropic <c>data</c> field on redacted_thinking blocks (opaque encrypted blob).</summary>
    public const string AnthropicRedactedData = "transit.thinking.anthropic.data";

    /// <summary>Gemini <c>thoughtSignature</c> field (opaque blob).</summary>
    public const string GeminiThoughtSignature = "transit.thinking.gemini.signature";

    /// <summary>OpenAI o1/o3 encrypted_content on reasoning items.</summary>
    public const string OpenAiEncryptedContent = "transit.thinking.openai.encrypted_content";

    /// <summary>OpenAI Responses reasoning item ID (e.g. <c>rs_...</c>).</summary>
    public const string OpenAiReasoningItemId = "transit.thinking.openai.item_id";

    /// <summary>OpenAI Responses reasoning summary parts (array preserved as-is).</summary>
    public const string OpenAiReasoningSummary = "transit.thinking.openai.summary";

    // ── Gemini thought signatures on the OpenAI compatibility surface ─────────
    //
    // Gemini 3 signs the functionCall part and rejects the next turn unless the
    // signature comes back on that same part. Chat Completions has no field of its
    // own for it, so Google defines a carrier on the tool call:
    //
    //   tool_calls[].extra_content.google.thought_signature
    //
    // documented under "Signatures for OpenAI compatibility" at
    // https://ai.google.dev/gemini-api/docs/thought-signatures. Google's own
    // OpenAI-compat endpoint both returns it there and validates it there on replay,
    // so speaking the same shape means clients already patched for Gemini (and the
    // SDKs that preserve unknown tool-call fields) work against us unchanged.
    //
    // Google documents this shape for Chat Completions only — there is no equivalent
    // for the Responses API or for Anthropic Messages. Those keep no carrier at all
    // and fall back to the sentinel Google prescribes for histories that have no
    // signatures (see GeminiThoughtSignaturePolicy). Inventing a field there would be
    // a shape no client knows how to produce.

    /// <summary>Google's namespace object on an OpenAI-compat <c>tool_calls[]</c> entry.</summary>
    public const string OpenAiExtraContentKey = "extra_content";

    /// <summary>Vendor key inside <see cref="OpenAiExtraContentKey"/>.</summary>
    public const string OpenAiExtraContentVendorKey = "google";

    /// <summary>Signature field inside <see cref="OpenAiExtraContentVendorKey"/>.</summary>
    public const string OpenAiThoughtSignatureKey = "thought_signature";

    /// <summary>
    /// Attaches <c>extra_content.google.thought_signature</c> to an OpenAI-compat
    /// <c>tool_calls[]</c> entry under construction, in the shape Google documents.
    /// A no-op without a signature, so the key stays off the wire for every backend
    /// that does not sign tool calls.
    /// </summary>
    public static void WriteOpenAiToolCallSignature(
        IDictionary<string, object?> wireToolCall, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return;

        wireToolCall[OpenAiExtraContentKey] = new Dictionary<string, object?>
        {
            [OpenAiExtraContentVendorKey] = new Dictionary<string, object?>
            {
                [OpenAiThoughtSignatureKey] = signature,
            },
        };
    }

    /// <summary>
    /// Reads <c>extra_content.google.thought_signature</c> back off an OpenAI-compat
    /// <c>tool_calls[]</c> entry and pins it to the call, from where
    /// <c>GeminiOutboundEncoder</c> restores it to the native <c>functionCall</c> part.
    /// </summary>
    public static void ReadOpenAiToolCallSignature(AIContent content, JsonElement toolCall)
    {
        if (toolCall.ValueKind == JsonValueKind.Object
            && toolCall.TryGetProperty(OpenAiExtraContentKey, out var extra)
            && extra.ValueKind == JsonValueKind.Object
            && extra.TryGetProperty(OpenAiExtraContentVendorKey, out var google)
            && google.ValueKind == JsonValueKind.Object
            && google.TryGetProperty(OpenAiThoughtSignatureKey, out var sig)
            && sig.ValueKind == JsonValueKind.String
            && sig.GetString() is { Length: > 0 } value)
        {
            SetGeminiThoughtSignature(content, value);
        }
    }

    // ── Kind enum (stored as string in AdditionalProperties) ──────────────────

    public const string KindThinking = "thinking";
    public const string KindRedacted = "redacted";

    // ── Factory helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a TextContent representing a regular thinking block. The optional
    /// <paramref name="anthropicSignature"/> and <paramref name="geminiSignature"/>
    /// preserve the cryptographic blobs that providers require on round-trip.
    /// </summary>
    public static TextContent CreateThinkingContent(
        string thinkingText,
        string? anthropicSignature = null,
        string? geminiSignature = null,
        string? openAiEncryptedContent = null)
    {
        var content = new TextContent(thinkingText);
        content.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        content.AdditionalProperties[ThinkingMarker] = true;
        content.AdditionalProperties[ThinkingKind] = KindThinking;
        if (!string.IsNullOrEmpty(anthropicSignature))
            content.AdditionalProperties[AnthropicSignature] = anthropicSignature;
        if (!string.IsNullOrEmpty(geminiSignature))
            content.AdditionalProperties[GeminiThoughtSignature] = geminiSignature;
        if (!string.IsNullOrEmpty(openAiEncryptedContent))
            content.AdditionalProperties[OpenAiEncryptedContent] = openAiEncryptedContent;
        return content;
    }

    /// <summary>
    /// Creates a TextContent representing an Anthropic <c>redacted_thinking</c> block.
    /// The opaque <paramref name="anthropicData"/> is preserved verbatim so the
    /// outbound encoder can rebuild the original block byte-for-byte. The
    /// human-readable Text is set to "[redacted]" purely for debugging visibility.
    /// </summary>
    public static TextContent CreateRedactedThinkingContent(string anthropicData)
    {
        var content = new TextContent("[redacted]");
        content.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        content.AdditionalProperties[ThinkingMarker] = true;
        content.AdditionalProperties[ThinkingKind] = KindRedacted;
        content.AdditionalProperties[AnthropicRedactedData] = anthropicData;
        return content;
    }

    // ── Inspection helpers ────────────────────────────────────────────────────

    /// <summary>Returns true if a content item was tagged as any kind of thinking block.</summary>
    public static bool IsThinkingContent(AIContent content)
        => content.AdditionalProperties?.TryGetValue(ThinkingMarker, out var v) == true && v is true;

    /// <summary>Returns true if this is specifically an Anthropic redacted_thinking block.</summary>
    public static bool IsRedactedThinking(AIContent content)
        => IsThinkingContent(content)
           && content.AdditionalProperties is { } props
           && props.TryGetValue(ThinkingKind, out var k)
           && k is string ks
           && string.Equals(ks, KindRedacted, StringComparison.Ordinal);

    /// <summary>Returns the thinking text if this is a thinking block, null otherwise.</summary>
    public static string? GetThinkingText(AIContent content)
        => IsThinkingContent(content) ? ((TextContent)content).Text : null;

    public static string? GetAnthropicSignature(AIContent content)
        => GetAdditional(content, AnthropicSignature) as string;

    public static string? GetAnthropicRedactedData(AIContent content)
        => GetAdditional(content, AnthropicRedactedData) as string;

    public static string? GetGeminiThoughtSignature(AIContent content)
        => GetAdditional(content, GeminiThoughtSignature) as string;

    public static string? GetOpenAiEncryptedContent(AIContent content)
        => GetAdditional(content, OpenAiEncryptedContent) as string;

    public static string? GetOpenAiReasoningItemId(AIContent content)
        => GetAdditional(content, OpenAiReasoningItemId) as string;

    /// <summary>
    /// Returns the opaque thinking signature regardless of which protocol's carrier
    /// it arrived under (Gemini <c>thoughtSignature</c> / Anthropic <c>signature</c>
    /// / OpenAI <c>encrypted_content</c>), or null if none is present.
    ///
    /// <para>The blob is model-bound, not protocol-bound: within a conversation the
    /// backend model is fixed, so the signature it produced rides back to the client
    /// in <em>the client's</em> protocol field and returns under that same key. Every
    /// outbound encoder therefore recovers it from any carrier and re-emits it in its
    /// own native field, so a cross-protocol caller (e.g. an OpenAI client routed onto
    /// a Claude backend) does not lose the signature and trigger the upstream's
    /// "thinking.signature: Field required" rejection.</para>
    /// </summary>
    public static string? GetAnySignature(AIContent content)
        => GetGeminiThoughtSignature(content)
           ?? GetAnthropicSignature(content)
           ?? GetOpenAiEncryptedContent(content);

    /// <summary>Sets a thinking signature on an existing thinking content (mutates).</summary>
    public static void SetAnthropicSignature(AIContent content, string? signature)
        => SetAdditional(content, AnthropicSignature, signature);

    public static void SetGeminiThoughtSignature(AIContent content, string? signature)
        => SetAdditional(content, GeminiThoughtSignature, signature);

    public static void SetOpenAiEncryptedContent(AIContent content, string? value)
        => SetAdditional(content, OpenAiEncryptedContent, value);

    public static void SetOpenAiReasoningItemId(AIContent content, string? itemId)
        => SetAdditional(content, OpenAiReasoningItemId, itemId);

    // ── Internal ──────────────────────────────────────────────────────────────

    private static object? GetAdditional(AIContent content, string key)
        => content.AdditionalProperties?.TryGetValue(key, out var v) == true ? v : null;

    private static void SetAdditional(AIContent content, string key, object? value)
    {
        if (value is null)
        {
            content.AdditionalProperties?.Remove(key);
            return;
        }
        content.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        content.AdditionalProperties[key] = value;
    }
}
