namespace ChatTransit.Responses;

/// <summary>
/// Shared helper for the response decoders (<see cref="AnthropicSseDecoder"/>,
/// <see cref="OpenAiChatSseDecoder"/>, <see cref="OpenAiResponsesSseDecoder"/>,
/// <see cref="GeminiSseDecoder"/>): pulls the JSON payload(s) out of a raw SSE
/// frame. A frame is whatever a provider's raw SSE passthrough yields per chunk —
/// normally one complete <c>event:</c>/<c>data:</c> block, but this tolerates
/// multiple <c>data:</c> lines in one frame and skips the <c>[DONE]</c> sentinel.
/// </summary>
internal static class SseFrameParsing
{
    public static IEnumerable<string> ExtractDataPayloads(string frame)
    {
        foreach (var rawLine in frame.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
            if (payload.Length == 0 || payload == "[DONE]") continue;
            yield return payload;
        }
    }
}
