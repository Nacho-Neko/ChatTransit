using ChatTransit.Mapping;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatTransit.Responses;

/// <summary>
/// Parses the Anthropic Messages API's own SSE event stream (and non-streaming
/// message body) back into canonical <see cref="StreamingChunkDto"/> values.
/// Inverse of <see cref="AnthropicSseEncoder"/> — see that type for the full
/// event-sequence documentation this decodes.
/// </summary>
public static class AnthropicSseDecoder
{
    public static async IAsyncEnumerable<StreamingChunkDto> DecodeAsync(
        IAsyncEnumerable<string> nativeSseFrames,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // content_block_start declares the block's type once; later deltas on the
        // same index only carry the delta payload, so we need this to know how to
        // interpret them.
        var blockTypeByIndex = new Dictionary<int, string>();

        await foreach (var frame in nativeSseFrames.WithCancellation(ct))
        {
            foreach (var payload in SseFrameParsing.ExtractDataPayloads(frame))
            {
                StreamingChunkDto? chunk;
                try { chunk = DecodeEvent(payload, blockTypeByIndex); }
                catch (JsonException) { continue; }
                if (chunk != null) yield return chunk;
            }
        }
    }

    /// <summary>
    /// Non-streaming: parses a full Anthropic <c>Message</c> JSON body into chunks.
    /// </summary>
    public static List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
    {
        var chunks = new List<StreamingChunkDto>();
        using var doc = JsonDocument.Parse(nativeJsonBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentEl.EnumerateArray())
            {
                var type = UsageDictBuilder.GetString(block, "type");
                switch (type)
                {
                    case "text":
                        chunks.Add(new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.Text,
                            Text = UsageDictBuilder.GetString(block, "text") ?? "",
                        });
                        break;

                    case "thinking":
                        chunks.Add(new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.Thinking,
                            Text = UsageDictBuilder.GetString(block, "thinking"),
                            ReasoningSignature = UsageDictBuilder.GetString(block, "signature"),
                        });
                        break;

                    case "redacted_thinking":
                        // redacted_thinking has no "thinking"/"signature" — its opaque
                        // payload lives in "data" and must be preserved for replay.
                        chunks.Add(new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.Thinking,
                            RedactedThinkingData = UsageDictBuilder.GetString(block, "data"),
                        });
                        break;

                    case "tool_use":
                        var input = block.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : "{}";
                        chunks.Add(new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.FunctionCall,
                            FunctionName = UsageDictBuilder.GetString(block, "name"),
                            FunctionCallId = UsageDictBuilder.GetString(block, "id"),
                            FunctionArguments = input,
                        });
                        break;
                }
            }
        }

        var stopReason = UsageDictBuilder.GetString(root, "stop_reason");
        Dictionary<string, long>? usage = null;
        if (root.TryGetProperty("usage", out var usageEl))
            usage = ReadUsage(usageEl);

        if (stopReason != null || usage != null)
            chunks.Add(new StreamingChunkDto
            {
                ContentType = StreamingContentType.Usage,
                FinishReason = stopReason,
                Usage = usage,
            });

        return chunks;
    }

    private static StreamingChunkDto? DecodeEvent(string json, Dictionary<int, string> blockTypeByIndex)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = UsageDictBuilder.GetString(root, "type");

        switch (type)
        {
            case "message_start":
            {
                // The first (and only) place Anthropic reports input_tokens /
                // cache_creation_input_tokens / cache_read_input_tokens is the
                // message_start's message.usage. Surface it as a Usage chunk or the
                // prompt-side token counts are lost for cross-protocol re-render.
                if (root.TryGetProperty("message", out var startMsg)
                    && startMsg.ValueKind == JsonValueKind.Object
                    && startMsg.TryGetProperty("usage", out var startUsageEl))
                {
                    var startUsage = ReadUsage(startUsageEl);
                    if (startUsage != null)
                        return new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.Usage,
                            Usage = startUsage,
                        };
                }
                return null;
            }

            case "error":
                // Preserve the upstream data payload verbatim for re-emission.
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Usage,
                    Error = json,
                };

            case "content_block_start":
            {
                var index = root.GetProperty("index").GetInt32();
                var block = root.GetProperty("content_block");
                var blockType = UsageDictBuilder.GetString(block, "type") ?? "";
                blockTypeByIndex[index] = blockType;

                if (blockType == "tool_use")
                    return new StreamingChunkDto
                    {
                        ContentType = StreamingContentType.FunctionCall,
                        FunctionName = UsageDictBuilder.GetString(block, "name"),
                        FunctionCallId = UsageDictBuilder.GetString(block, "id"),
                    };
                // redacted_thinking arrives complete in content_block_start (no
                // deltas follow); capture its opaque "data" or it is lost for replay.
                if (blockType == "redacted_thinking")
                    return new StreamingChunkDto
                    {
                        ContentType = StreamingContentType.Thinking,
                        RedactedThinkingData = UsageDictBuilder.GetString(block, "data"),
                    };
                return null;
            }

            case "content_block_delta":
            {
                var delta = root.GetProperty("delta");
                var deltaType = UsageDictBuilder.GetString(delta, "type");
                return deltaType switch
                {
                    "text_delta" => new StreamingChunkDto
                    {
                        ContentType = StreamingContentType.Text,
                        Text = UsageDictBuilder.GetString(delta, "text"),
                    },
                    "input_json_delta" => new StreamingChunkDto
                    {
                        ContentType = StreamingContentType.FunctionCall,
                        FunctionArguments = UsageDictBuilder.GetString(delta, "partial_json"),
                    },
                    "thinking_delta" => new StreamingChunkDto
                    {
                        ContentType = StreamingContentType.Thinking,
                        Text = UsageDictBuilder.GetString(delta, "thinking"),
                    },
                    "signature_delta" => new StreamingChunkDto
                    {
                        ContentType = StreamingContentType.Thinking,
                        ReasoningSignature = UsageDictBuilder.GetString(delta, "signature"),
                    },
                    _ => null,
                };
            }

            case "message_delta":
            {
                var delta = root.GetProperty("delta");
                var stopReason = UsageDictBuilder.GetString(delta, "stop_reason");
                Dictionary<string, long>? usage = null;
                if (root.TryGetProperty("usage", out var usageEl))
                    usage = ReadUsage(usageEl);
                if (stopReason == null && usage == null) return null;
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Usage,
                    FinishReason = stopReason,
                    Usage = usage,
                };
            }

            // ping / content_block_stop / message_stop carry no information a
            // cross-protocol re-render needs.
            default:
                return null;
        }
    }

    private static Dictionary<string, long>? ReadUsage(JsonElement usage) => UsageDictBuilder.Build(
        inputTokens: UsageDictBuilder.GetLong(usage, "input_tokens"),
        outputTokens: UsageDictBuilder.GetLong(usage, "output_tokens"),
        cacheCreationInputTokens: UsageDictBuilder.GetLong(usage, "cache_creation_input_tokens"),
        cacheReadInputTokens: UsageDictBuilder.GetLong(usage, "cache_read_input_tokens"));
}
