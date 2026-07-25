namespace ChatTransit.Mapping;

/// <summary>
/// Canonical <see cref="StreamingChunkDto.Usage"/> dictionary key names, plus the
/// legacy/alternate spellings response decoders may see on the wire.
///
/// <para>ChatTransit defines its own copy rather than referencing a platform usage-key
/// constants class: the values below are a private implementation detail of how this
/// library shapes <c>Usage</c> dictionaries, not a platform-wide contract — keeping them
/// local is what lets ChatTransit carry zero project references.</para>
/// </summary>
internal static class ChatTransitUsageKeys
{
    public const string InputTokens = "inputTokens";
    public const string OutputTokens = "outputTokens";
    public const string CacheCreationInputTokens = "cacheCreationInputTokens";
    public const string CacheReadInputTokens = "cacheReadInputTokens";
    public const string ReasoningTokens = "reasoningTokens";

    public static readonly string[] InputCandidates =
        [InputTokens, "input_tokens", "prompt_tokens", "promptTokens"];

    public static readonly string[] OutputCandidates =
        [OutputTokens, "output_tokens", "completion_tokens", "completionTokens"];

    public static readonly string[] CacheReadInputCandidates =
        [CacheReadInputTokens, "cache_read_input_tokens", "cacheReadInputTokens"];
}
