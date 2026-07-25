namespace ChatTransit.Mapping;

/// <summary>
/// Derives provider-model capability decisions from the resolved upstream model
/// name (<see cref="TransitRequest.Model"/>, which the gateway rewrites to the
/// real vendor model <b>before</b> transcoding). This is deliberately pure string
/// parsing with no external catalog: the codebase has no per-model capability
/// metadata, so the model name is the only signal available at encode time.
///
/// <para><b>Why this is needed.</b> Modern providers moved reasoning control from
/// numeric token budgets to named effort levels and tightened validation:
/// <list type="bullet">
/// <item>Anthropic Claude 4.7+ / Sonnet 5 / Opus 5 <b>reject</b>
/// <c>temperature</c>/<c>top_p</c>/<c>top_k</c> and manual
/// <c>thinking:{type:"enabled"}</c> with a 400; they require
/// <c>thinking:{type:"adaptive"}</c>. Claude ≤ 4.6 is the opposite (adaptive 400s,
/// enabled works, sampling accepted).</item>
/// <item>Gemini 3+ uses <c>thinkingConfig.thinkingLevel</c>; Gemini 2.5 uses the
/// legacy <c>thinkingBudget</c>. Sending both in one request is a 400.</item>
/// </list></para>
///
/// <para><b>Unknown ⇒ legacy.</b> When the version can't be parsed (e.g. a channel
/// remapped the model to a non-standard name) we fall back to the pre-existing
/// behaviour rather than guess. This can 400 a modern model hiding behind a
/// non-standard name; that trade-off is accepted since the alternative — omitting
/// sampling for every unrecognised model — would silently drop knobs on the
/// common legacy case.</para>
/// </summary>
public static class ModelCapabilities
{
    private static readonly char[] TokenSeparators = ['-', '.', '_', ':', '/', '@', ' '];
    private static readonly string[] AnthropicFamilies = ["opus", "sonnet", "haiku"];

    /// <summary>
    /// True when the Anthropic target is Claude 4.7 or later (incl. Sonnet/Opus 5+),
    /// i.e. sampling params are unsupported and thinking must be <c>adaptive</c>.
    /// Unparseable names return false (legacy).
    /// </summary>
    public static bool IsModernAnthropic(string? model)
    {
        var v = ParseAnthropicVersion(model);
        return v is { } ver && IsAtLeast(ver, 4, 7);
    }

    /// <summary>
    /// True when the Gemini target is version 3 or later, i.e. it takes
    /// <c>thinkingLevel</c> rather than the legacy <c>thinkingBudget</c>.
    /// Unparseable names return false (treated as 2.5-era → budget).
    /// </summary>
    public static bool GeminiSupportsThinkingLevel(string? model)
    {
        var v = ParseGeminiVersion(model);
        return v is { } ver && ver.Major >= 3;
    }

    // ── Version parsing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the (major, minor) version out of an Anthropic model id, tolerating
    /// both historical orderings — version-before-family (<c>claude-3-5-sonnet</c>)
    /// and version-after-family (<c>claude-sonnet-4-5</c>, <c>claude-opus-4-1</c>) —
    /// and ignoring trailing date stamps (<c>-20250514</c>). Returns null when no
    /// version can be found. No <c>claude</c> substring guard: the caller only
    /// invokes this on the Anthropic encode path, so a remapped name without
    /// "claude" must still be parsed rather than skipped.
    /// </summary>
    internal static (int Major, int Minor)? ParseAnthropicVersion(string? model)
    {
        var tokens = Tokenize(model);
        if (tokens.Count == 0) return null;

        var anchor = tokens.FindIndex(t => Array.IndexOf(AnthropicFamilies, t) >= 0);
        if (anchor < 0) anchor = tokens.FindIndex(t => t == "claude");
        if (anchor < 0) return null;

        // Prefer numbers immediately before the family keyword, else after it.
        var before = CollectAdjacentNumbers(tokens, anchor, forward: false);
        if (before.Count > 0) return ToVersion(before);
        var after = CollectAdjacentNumbers(tokens, anchor, forward: true);
        if (after.Count > 0) return ToVersion(after);
        return null;
    }

    /// <summary>
    /// Parses the (major, minor) version out of a Gemini model id
    /// (<c>gemini-2.5-flash</c>, <c>gemini-3-pro-preview</c>). Returns null when no
    /// version can be found.
    /// </summary>
    internal static (int Major, int Minor)? ParseGeminiVersion(string? model)
    {
        var tokens = Tokenize(model);
        if (tokens.Count == 0) return null;

        var anchor = tokens.FindIndex(t => t == "gemini");
        if (anchor < 0) return null;

        var after = CollectAdjacentNumbers(tokens, anchor, forward: true);
        return after.Count > 0 ? ToVersion(after) : null;
    }

    private static List<string> Tokenize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return [];
        var raw = model.ToLowerInvariant().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(raw.Length);
        foreach (var tk in raw)
        {
            // Drop date-like tokens (6-8 consecutive digits, e.g. 20250514) so a
            // release date is never mistaken for a version number.
            if (tk.Length >= 6 && IsAllDigits(tk)) continue;
            result.Add(tk);
        }
        return result;
    }

    /// <summary>
    /// Collects the contiguous run of purely-numeric tokens immediately adjacent to
    /// <paramref name="anchor"/> (backwards or forwards), returned in left-to-right
    /// order so index 0 is the major component.
    /// </summary>
    private static List<int> CollectAdjacentNumbers(List<string> tokens, int anchor, bool forward)
    {
        var nums = new List<int>();
        var step = forward ? 1 : -1;
        for (var i = anchor + step; i >= 0 && i < tokens.Count; i += step)
        {
            if (!IsAllDigits(tokens[i]) || !int.TryParse(tokens[i], out var n)) break;
            if (forward) nums.Add(n); else nums.Insert(0, n);
        }
        return nums;
    }

    private static (int Major, int Minor) ToVersion(List<int> parts)
        => (parts[0], parts.Count > 1 ? parts[1] : 0);

    private static bool IsAtLeast((int Major, int Minor) v, int major, int minor)
        => v.Major > major || (v.Major == major && v.Minor >= minor);

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
            if (c < '0' || c > '9') return false;
        return s.Length > 0;
    }
}
