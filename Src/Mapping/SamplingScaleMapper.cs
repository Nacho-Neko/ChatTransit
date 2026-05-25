namespace Gateway.Shared.ChatTransit.Mapping;

/// <summary>
/// Centralises the protocol-vs-IR scale conversion for sampling parameters
/// (<c>temperature</c>, <c>top_p</c>, <c>top_k</c>). Different upstream APIs
/// publish different legal ranges and a literal pass-through ends up rejected
/// by the backend whenever the client uses the wider source range — most
/// notably <c>temperature=1.5</c> from an OpenAI caller routed to an Anthropic
/// backend (Anthropic caps temperature at 1.0).
///
/// <para><b>IR canonical scale</b> (the format every <see cref="TransitRequest.Options"/>
/// value is in):</para>
/// <list type="bullet">
///   <item><b>Temperature</b>: <c>[0, 2]</c> — OpenAI Chat / OpenAI Responses /
///         Gemini 1.5+ all use this range natively; only Anthropic differs
///         (caps at 1.0) so it's the one inbound decoder that does <c>× 2</c>
///         and the one outbound encoder that does <c>÷ 2</c>.</item>
///   <item><b>TopP</b>: <c>[0, 1]</c> — universal across providers; we only
///         clamp defensively (no rescale).</item>
///   <item><b>TopK</b>: positive integer — OpenAI Chat/Responses don't have
///         the field at all and silently drop it on encode; Anthropic and
///         Gemini both accept the raw integer with model-specific upper bounds
///         that we deliberately don't enforce client-side (let the upstream
///         400 it instead of guessing the cap per model).</item>
/// </list>
///
/// <para><b>Why proportional rescale and not clamp?</b> "OpenAI temperature 1.0"
/// is semantically halfway between deterministic and max-creative on a 0–2
/// scale; the equivalent point on Anthropic's 0–1 scale is 0.5, not 1.0.
/// Clamping <c>min(t, 1.0)</c> would silently degrade requests where a client
/// asked for "moderate" randomness into "maximum" randomness on Anthropic.</para>
/// </summary>
internal static class SamplingScaleMapper
{
    private const float IrTemperatureMax = 2.0f;
    private const float AnthropicTemperatureMax = 1.0f;
    private static readonly float AnthropicToIrFactor = IrTemperatureMax / AnthropicTemperatureMax; // 2.0
    private static readonly float IrToAnthropicFactor = AnthropicTemperatureMax / IrTemperatureMax; // 0.5

    /// <summary>
    /// Anthropic body value (<c>[0, 1]</c>) → IR temperature (<c>[0, 2]</c>).
    /// Defensive clamp on the result keeps the IR in spec even if the inbound
    /// client somehow supplied an out-of-range Anthropic value that the
    /// upstream would otherwise reject.
    /// </summary>
    public static float NormalizeTemperatureFromAnthropic(float anthropicTemperature)
        => Math.Clamp(anthropicTemperature * AnthropicToIrFactor, 0f, IrTemperatureMax);

    /// <summary>
    /// IR temperature (<c>[0, 2]</c>) → Anthropic body value (<c>[0, 1]</c>).
    /// </summary>
    public static float DenormalizeTemperatureToAnthropic(float irTemperature)
        => Math.Clamp(irTemperature * IrToAnthropicFactor, 0f, AnthropicTemperatureMax);

    /// <summary>
    /// Clamps IR temperature into <c>[0, 2]</c>. Used by OpenAI Chat / Responses
    /// / Gemini encoders that share the IR's scale but want to refuse to
    /// forward obviously-malformed values to the upstream.
    /// </summary>
    public static float ClampTemperatureForOpenAiScale(float temperature)
        => Math.Clamp(temperature, 0f, IrTemperatureMax);

    /// <summary>Defensive <c>[0, 1]</c> clamp — every provider uses the same scale here.</summary>
    public static float ClampTopP(float topP) => Math.Clamp(topP, 0f, 1f);

    /// <summary>
    /// <c>top_k</c> must be a positive integer; everything below 1 is a client
    /// bug and we squash it rather than forward "top_k=0" to the upstream.
    /// </summary>
    public static int ClampTopK(int topK) => Math.Max(topK, 1);
}
