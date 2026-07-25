namespace ChatTransit.Mapping;

/// <summary>
/// Shared translation of reasoning "effort" across providers so the Anthropic and
/// Gemini encoders don't drift.
/// <list type="bullet">
/// <item>OpenAI <c>reasoning_effort</c>: <c>none/minimal/low/medium/high/xhigh/max</c>
/// (default <c>medium</c>).</item>
/// <item>Gemini <c>thinkingConfig.thinkingLevel</c> enum: <c>minimal/low/medium/high</c>.</item>
/// <item>Anthropic legacy <c>thinking.budget_tokens</c>: a token count.</item>
/// </list>
/// </summary>
public static class ReasoningEffortMapper
{
    /// <summary>
    /// Maps an effort/level string to an Anthropic legacy <c>budget_tokens</c>.
    /// Ladder mirrors OpenAI's published guidance (minimal≈1k … high≈16k) and
    /// extends it for the newer <c>xhigh</c>/<c>max</c> tiers. Returns null for
    /// <c>none</c> and unrecognised values so thinking stays off rather than being
    /// silently forced on (or, previously, silently dropped for xhigh/max).
    /// </summary>
    public static int? EffortToBudget(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "minimal" => 1024,
        "low" => 4096,
        "medium" => 8192,
        "high" => 16384,
        "xhigh" => 24576,
        "max" => 32768,
        _ => null,
    };

    /// <summary>
    /// Normalises an effort/level string to a valid Gemini <c>thinkingLevel</c>
    /// (<c>minimal/low/medium/high</c>). <c>xhigh</c>/<c>max</c> clamp to
    /// <c>high</c>; <c>none</c> and unrecognised values return null (so no invalid
    /// enum value like "xhigh"/"none" is ever written — which the API rejects).
    /// </summary>
    public static string? ToGeminiLevel(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "minimal" => "minimal",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "high",
        "max" => "high",
        _ => null,
    };

    /// <summary>Approximates a Gemini <c>thinkingLevel</c> from a legacy token budget
    /// (for a 2.5-shaped request routed onto a Gemini 3 target).</summary>
    public static string? BudgetToGeminiLevel(int? budget) => budget switch
    {
        null => null,
        <= 2048 => "minimal",
        <= 6144 => "low",
        <= 12288 => "medium",
        _ => "high",
    };
}
