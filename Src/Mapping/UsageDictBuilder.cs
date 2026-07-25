using System.Text.Json;

namespace ChatTransit.Mapping;

/// <summary>
/// Builds a canonical <see cref="StreamingChunkDto.Usage"/>
/// dictionary (camelCase <see cref="ChatTransitUsageKeys"/>) from whatever token counts a
/// response decoder pulled out of a native usage object. Shared by all four
/// response decoders so the resulting chunks read the same way regardless of which
/// native protocol they were decoded from.
/// </summary>
internal static class UsageDictBuilder
{
    public static Dictionary<string, long>? Build(
        long? inputTokens = null, long? outputTokens = null,
        long? cacheCreationInputTokens = null, long? cacheReadInputTokens = null,
        long? reasoningTokens = null)
    {
        if (inputTokens is null && outputTokens is null && cacheCreationInputTokens is null
            && cacheReadInputTokens is null && reasoningTokens is null)
            return null;

        var usage = new Dictionary<string, long>(StringComparer.Ordinal);
        if (inputTokens.HasValue) usage[ChatTransitUsageKeys.InputTokens] = inputTokens.Value;
        if (outputTokens.HasValue) usage[ChatTransitUsageKeys.OutputTokens] = outputTokens.Value;
        if (cacheCreationInputTokens.HasValue) usage[ChatTransitUsageKeys.CacheCreationInputTokens] = cacheCreationInputTokens.Value;
        if (cacheReadInputTokens.HasValue) usage[ChatTransitUsageKeys.CacheReadInputTokens] = cacheReadInputTokens.Value;
        if (reasoningTokens.HasValue) usage[ChatTransitUsageKeys.ReasoningTokens] = reasoningTokens.Value;
        return usage;
    }

    public static long? GetLong(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(prop, out var el)
        && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt64(out var v)
            ? v
            : null;

    public static string? GetString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(prop, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
