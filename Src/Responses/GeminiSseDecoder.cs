using ChatTransit.Mapping;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatTransit.Responses;

/// <summary>
/// Parses Gemini's own <c>generateContent</c> SSE stream (and the identically
/// shaped non-streaming body — a single <c>GenerateContentResponse</c>) back into
/// canonical <see cref="StreamingChunkDto"/> values. Inverse of
/// <see cref="GeminiSseEncoder"/>.
/// </summary>
public static class GeminiSseDecoder
{
    public static async IAsyncEnumerable<StreamingChunkDto> DecodeAsync(
        IAsyncEnumerable<string> nativeSseFrames,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var frame in nativeSseFrames.WithCancellation(ct))
        {
            foreach (var payload in SseFrameParsing.ExtractDataPayloads(frame))
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(payload); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    foreach (var chunk in DecodeGenerateContentResponse(doc.RootElement))
                        yield return chunk;
                }
            }
        }
    }

    /// <summary>
    /// Non-streaming: a Gemini non-streaming body is one whole
    /// <c>GenerateContentResponse</c> — same shape as a single streamed chunk.
    /// </summary>
    public static List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
    {
        using var doc = JsonDocument.Parse(nativeJsonBody);
        return DecodeGenerateContentResponse(doc.RootElement).ToList();
    }

    private static IEnumerable<StreamingChunkDto> DecodeGenerateContentResponse(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var candidatesEl)
            && candidatesEl.ValueKind == JsonValueKind.Array
            && candidatesEl.GetArrayLength() > 0)
        {
            var candidate = candidatesEl[0];

            if (candidate.TryGetProperty("content", out var contentEl)
                && contentEl.TryGetProperty("parts", out var partsEl)
                && partsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in partsEl.EnumerateArray())
                {
                    var isThought = part.TryGetProperty("thought", out var thoughtEl)
                        && thoughtEl.ValueKind == JsonValueKind.True;
                    var text = UsageDictBuilder.GetString(part, "text");
                    var signature = UsageDictBuilder.GetString(part, "thoughtSignature");

                    if (isThought)
                    {
                        if (text != null || signature != null)
                            yield return new StreamingChunkDto
                            {
                                ContentType = StreamingContentType.Thinking,
                                Text = text,
                                ReasoningSignature = signature,
                            };
                    }
                    else if (text != null)
                    {
                        // Gemini 3 may attach a thoughtSignature to an ordinary
                        // (non-thought) text part too; carry it so the next turn can
                        // replay it (thinking continuity), same as functionCall parts.
                        yield return new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.Text,
                            Text = text,
                            ReasoningSignature = signature,
                        };
                    }

                    if (part.TryGetProperty("functionCall", out var fcEl) && fcEl.ValueKind == JsonValueKind.Object)
                    {
                        yield return new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.FunctionCall,
                            FunctionName = UsageDictBuilder.GetString(fcEl, "name"),
                            FunctionCallId = UsageDictBuilder.GetString(fcEl, "id"),
                            FunctionArguments = fcEl.TryGetProperty("args", out var argsEl) ? argsEl.GetRawText() : "{}",
                            // Gemini 3 attaches the thoughtSignature to the functionCall
                            // part itself; it must ride back with the tool call so the
                            // next turn can replay it (otherwise the upstream 400s).
                            ReasoningSignature = signature,
                        };
                    }
                }
            }

            var finishReason = UsageDictBuilder.GetString(candidate, "finishReason");
            if (finishReason != null)
                yield return new StreamingChunkDto { ContentType = StreamingContentType.Usage, FinishReason = finishReason };
        }
        else if (root.TryGetProperty("promptFeedback", out var pfEl)
            && pfEl.ValueKind == JsonValueKind.Object
            && UsageDictBuilder.GetString(pfEl, "blockReason") is { Length: > 0 } blockReason)
        {
            // Prompt was rejected by the safety filter: the body carries no
            // candidates, only promptFeedback.blockReason. Surface it as a
            // content_filter finish plus an error so it is never silently
            // swallowed into an empty-but-successful response.
            yield return new StreamingChunkDto
            {
                ContentType = StreamingContentType.Usage,
                FinishReason = "content_filter",
                Error = $"Gemini blocked the prompt: {blockReason}",
                ErrorCode = blockReason,
            };
        }

        if (root.TryGetProperty("usageMetadata", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var usage = UsageDictBuilder.Build(
                inputTokens: UsageDictBuilder.GetLong(usageEl, "promptTokenCount"),
                outputTokens: UsageDictBuilder.GetLong(usageEl, "candidatesTokenCount"),
                cacheReadInputTokens: UsageDictBuilder.GetLong(usageEl, "cachedContentTokenCount"),
                reasoningTokens: UsageDictBuilder.GetLong(usageEl, "thoughtsTokenCount"));
            if (usage != null)
                yield return new StreamingChunkDto { ContentType = StreamingContentType.Usage, Usage = usage };
        }
    }
}
