using ChatTransit.Mapping;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatTransit.Responses;

/// <summary>
/// Parses OpenAI Chat Completions' own SSE chunk stream (and non-streaming
/// <c>chat.completion</c> body) back into canonical <see cref="StreamingChunkDto"/>
/// values. Inverse of <see cref="OpenAiChatSseEncoder"/>.
/// </summary>
public static class OpenAiChatSseDecoder
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
                    foreach (var chunk in DecodeChunk(doc.RootElement))
                        yield return chunk;
                }
            }
        }
    }

    /// <summary>
    /// Non-streaming: parses a full <c>chat.completion</c> JSON body into chunks.
    /// </summary>
    public static List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
    {
        var chunks = new List<StreamingChunkDto>();
        using var doc = JsonDocument.Parse(nativeJsonBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choicesEl)
            && choicesEl.ValueKind == JsonValueKind.Array
            && choicesEl.GetArrayLength() > 0)
        {
            var choice = choicesEl[0];
            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                var content = UsageDictBuilder.GetString(message, "content");
                if (content != null)
                    chunks.Add(new StreamingChunkDto { ContentType = StreamingContentType.Text, Text = content });

                var reasoningContent = UsageDictBuilder.GetString(message, "reasoning_content");
                var reasoningSignature = message.TryGetProperty("reasoning", out var reasoningEl)
                    ? UsageDictBuilder.GetString(reasoningEl, "encrypted_content")
                    : null;
                if (reasoningContent != null || reasoningSignature != null)
                    chunks.Add(new StreamingChunkDto
                    {
                        ContentType = StreamingContentType.Thinking,
                        Text = reasoningContent,
                        ReasoningSignature = reasoningSignature,
                    });

                if (message.TryGetProperty("tool_calls", out var toolCallsEl)
                    && toolCallsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCallsEl.EnumerateArray())
                    {
                        var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;
                        chunks.Add(new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.FunctionCall,
                            FunctionName = fn.ValueKind == JsonValueKind.Object ? UsageDictBuilder.GetString(fn, "name") : null,
                            FunctionCallId = UsageDictBuilder.GetString(tc, "id"),
                            FunctionArguments = fn.ValueKind == JsonValueKind.Object ? UsageDictBuilder.GetString(fn, "arguments") : null,
                        });
                    }
                }
            }

            var finishReason = UsageDictBuilder.GetString(choice, "finish_reason");
            if (finishReason != null)
                chunks.Add(new StreamingChunkDto { ContentType = StreamingContentType.Usage, FinishReason = finishReason });
        }

        if (root.TryGetProperty("usage", out var usageEl))
        {
            var usage = ReadChatUsage(usageEl);
            if (usage != null)
                chunks.Add(new StreamingChunkDto { ContentType = StreamingContentType.Usage, Usage = usage });
        }

        return chunks;
    }

    private static IEnumerable<StreamingChunkDto> DecodeChunk(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usageTopEl) && usageTopEl.ValueKind == JsonValueKind.Object)
        {
            var usage = ReadChatUsage(usageTopEl);
            if (usage != null)
                yield return new StreamingChunkDto { ContentType = StreamingContentType.Usage, Usage = usage };
        }

        if (!root.TryGetProperty("choices", out var choicesEl)
            || choicesEl.ValueKind != JsonValueKind.Array
            || choicesEl.GetArrayLength() == 0)
            yield break;

        var choice = choicesEl[0];

        // Single-candidate behaviour: only the index==0 choice is projected (missing
        // index defaults to 0). n>1 candidates arrive on their own chunks with
        // index>0 and are ignored so they can't be merged into one stream.
        if (choice.TryGetProperty("index", out var choiceIdx)
            && choiceIdx.ValueKind == JsonValueKind.Number
            && choiceIdx.TryGetInt32(out var idxVal) && idxVal != 0)
            yield break;

        var finishReason = UsageDictBuilder.GetString(choice, "finish_reason");
        if (finishReason != null)
            yield return new StreamingChunkDto { ContentType = StreamingContentType.Usage, FinishReason = finishReason };

        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            yield break;

        var content = UsageDictBuilder.GetString(delta, "content");
        if (content != null)
            yield return new StreamingChunkDto { ContentType = StreamingContentType.Text, Text = content };

        var reasoningContent = UsageDictBuilder.GetString(delta, "reasoning_content");
        var reasoningSignature = delta.TryGetProperty("reasoning", out var reasoningEl)
            ? UsageDictBuilder.GetString(reasoningEl, "encrypted_content")
            : null;
        if (reasoningContent != null || reasoningSignature != null)
            yield return new StreamingChunkDto
            {
                ContentType = StreamingContentType.Thinking,
                Text = reasoningContent,
                ReasoningSignature = reasoningSignature,
            };

        if (delta.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in toolCallsEl.EnumerateArray())
            {
                var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;
                var name = fn.ValueKind == JsonValueKind.Object ? UsageDictBuilder.GetString(fn, "name") : null;
                var args = fn.ValueKind == JsonValueKind.Object ? UsageDictBuilder.GetString(fn, "arguments") : null;
                var callId = UsageDictBuilder.GetString(tc, "id");
                if (name == null && args == null && callId == null) continue;
                yield return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.FunctionCall,
                    FunctionName = name,
                    FunctionCallId = callId,
                    FunctionArguments = args,
                };
            }
        }
    }

    private static Dictionary<string, long>? ReadChatUsage(JsonElement usage)
    {
        var promptTokensDetails = usage.TryGetProperty("prompt_tokens_details", out var ptd) ? ptd : default;
        var completionTokensDetails = usage.TryGetProperty("completion_tokens_details", out var ctd) ? ctd : default;
        return UsageDictBuilder.Build(
            inputTokens: UsageDictBuilder.GetLong(usage, "prompt_tokens"),
            outputTokens: UsageDictBuilder.GetLong(usage, "completion_tokens"),
            cacheReadInputTokens: promptTokensDetails.ValueKind == JsonValueKind.Object
                ? UsageDictBuilder.GetLong(promptTokensDetails, "cached_tokens") : null,
            reasoningTokens: completionTokensDetails.ValueKind == JsonValueKind.Object
                ? UsageDictBuilder.GetLong(completionTokensDetails, "reasoning_tokens") : null);
    }
}
