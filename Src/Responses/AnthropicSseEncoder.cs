using Gateway.Shared.ChatTransit.Mapping;
using Gateway.Shared.Messaging.Serialization;
using Gateway.Shared.Providers.Streaming;
using MessagePack;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Responses;

/// <summary>
/// Converts <see cref="StreamingChunkDto"/> events into the Anthropic Messages
/// API SSE event stream (and the non-streaming aggregated body).
/// <para>Emits the full event sequence per the public docs: <c>message_start</c>
/// → <c>ping</c> → repeating <c>content_block_start</c> / <c>content_block_delta</c>
/// (with <c>text_delta</c> / <c>input_json_delta</c> / <c>thinking_delta</c> /
/// <c>signature_delta</c>) / <c>content_block_stop</c> → <c>message_delta</c>
/// (with the upstream-mapped stop_reason) → <c>message_stop</c>. Forwards
/// raw SSE chunks for same-protocol passthrough scenarios.</para>
/// </summary>
public static class AnthropicSseEncoder
{
    private static string FormatSse(string eventType, object data)
        => $"event: {eventType}\ndata: {JsonSerializer.Serialize(data)}\n\n";

    public static async IAsyncEnumerable<string> StreamAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks, string model,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messageId = $"msg_{Guid.NewGuid():N}"[..28];
        var blockIndex = 0;
        var inBlock = false;
        var currentBlockType = "";
        // Pending signature for the currently-open thinking block. Flushed as a
        // single signature_delta event right before content_block_stop, per the
        // Anthropic streaming spec.
        string? pendingThinkingSignature = null;
        int inputTokens = 0, outputTokens = 0;
        int cacheCreationInputTokens = 0, cacheReadInputTokens = 0;
        bool hasCacheCreationInputTokens = false, hasCacheReadInputTokens = false;
        string? upstreamFinishReason = null;
        bool hadToolCalls = false;

        yield return FormatSse("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = messageId,
                type = "message",
                role = "assistant",
                content = Array.Empty<object>(),
                model,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new
                {
                    input_tokens = 0,
                    output_tokens = 0,
                    cache_creation_input_tokens = (int?)null,
                    cache_read_input_tokens = (int?)null
                }
            }
        });

        yield return FormatSse("ping", new { type = "ping" });

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Text when chunk.Text != null:
                    {
                        if (inBlock && currentBlockType != "text")
                        {
                            FlushSignatureIfThinking(currentBlockType, blockIndex, ref pendingThinkingSignature, out var sigEvt);
                            if (sigEvt != null) yield return sigEvt;
                            yield return CloseBlock(blockIndex);
                            blockIndex++;
                            inBlock = false;
                            currentBlockType = "";
                        }

                        if (!inBlock)
                        {
                            yield return FormatSse("content_block_start", new
                            {
                                type = "content_block_start",
                                index = blockIndex,
                                content_block = new { type = "text", text = "" }
                            });
                            inBlock = true;
                            currentBlockType = "text";
                        }

                        yield return FormatSse("content_block_delta", new
                        {
                            type = "content_block_delta",
                            index = blockIndex,
                            delta = new { type = "text_delta", text = chunk.Text }
                        });
                        break;
                    }

                case StreamingContentType.FunctionCall:
                    {
                        if (chunk.FunctionName != null)
                        {
                            if (inBlock)
                            {
                                FlushSignatureIfThinking(currentBlockType, blockIndex, ref pendingThinkingSignature, out var sigEvt);
                                if (sigEvt != null) yield return sigEvt;
                                yield return CloseBlock(blockIndex);
                                blockIndex++;
                                inBlock = false;
                                currentBlockType = "";
                            }

                            yield return FormatSse("content_block_start", new
                            {
                                type = "content_block_start",
                                index = blockIndex,
                                content_block = new
                                {
                                    type = "tool_use",
                                    id = chunk.FunctionCallId ?? $"toolu_{Guid.NewGuid():N}",
                                    name = chunk.FunctionName,
                                    input = new { }
                                }
                            });
                            inBlock = true;
                            currentBlockType = "tool_use";
                            hadToolCalls = true;
                        }

                        if (chunk.FunctionArguments != null)
                        {
                            yield return FormatSse("content_block_delta", new
                            {
                                type = "content_block_delta",
                                index = blockIndex,
                                delta = new { type = "input_json_delta", partial_json = chunk.FunctionArguments }
                            });
                        }
                        break;
                    }

                case StreamingContentType.Thinking:
                    {
                        if (inBlock && currentBlockType != "thinking")
                        {
                            FlushSignatureIfThinking(currentBlockType, blockIndex, ref pendingThinkingSignature, out var sigEvt);
                            if (sigEvt != null) yield return sigEvt;
                            yield return CloseBlock(blockIndex);
                            blockIndex++;
                            inBlock = false;
                            currentBlockType = "";
                        }

                        if (!inBlock && (chunk.Text != null || !string.IsNullOrEmpty(chunk.ReasoningSignature)))
                        {
                            yield return FormatSse("content_block_start", new
                            {
                                type = "content_block_start",
                                index = blockIndex,
                                content_block = new { type = "thinking", thinking = "" }
                            });
                            inBlock = true;
                            currentBlockType = "thinking";
                        }

                        if (chunk.Text != null && currentBlockType == "thinking")
                        {
                            yield return FormatSse("content_block_delta", new
                            {
                                type = "content_block_delta",
                                index = blockIndex,
                                delta = new { type = "thinking_delta", thinking = chunk.Text }
                            });
                        }

                        // Buffer the latest signature; emit as the final delta on this
                        // block (Anthropic spec: signature_delta arrives once, right
                        // before content_block_stop on the thinking block).
                        if (!string.IsNullOrEmpty(chunk.ReasoningSignature)
                            && currentBlockType == "thinking")
                        {
                            pendingThinkingSignature = chunk.ReasoningSignature;
                        }
                        break;
                    }

                case StreamingContentType.Usage when chunk.Usage != null:
                    {
                        inputTokens = ResolveUsageMonotonic(chunk.Usage, inputTokens, ProviderUsageKeys.InputTokens);
                        outputTokens = ResolveUsageMonotonic(chunk.Usage, outputTokens, ProviderUsageKeys.OutputTokens);
                        if (TryGetUsageInt(chunk.Usage, ProviderUsageKeys.CacheCreationInputTokens, out var cacheCreation))
                        {
                            cacheCreationInputTokens = cacheCreation;
                            hasCacheCreationInputTokens = true;
                        }

                        if (TryGetUsageInt(chunk.Usage, ProviderUsageKeys.CacheReadInputTokens, out var cacheRead))
                        {
                            cacheReadInputTokens = cacheRead;
                            hasCacheReadInputTokens = true;
                        }
                        break;
                    }

                case StreamingContentType.RawSse when chunk.Text != null:
                    // Same-protocol passthrough: emit the upstream's raw SSE verbatim.
                    // Ensure the chunk ends with a blank line so SSE framing is preserved.
                    yield return chunk.Text.EndsWith("\n\n", StringComparison.Ordinal)
                        ? chunk.Text
                        : chunk.Text + "\n\n";
                    break;
            }
        }

        if (inBlock)
        {
            FlushSignatureIfThinking(currentBlockType, blockIndex, ref pendingThinkingSignature, out var sigEvt);
            if (sigEvt != null) yield return sigEvt;
            yield return CloseBlock(blockIndex);
        }

        var stopReason = StopReasonMapper.DeriveAnthropicStopReason(upstreamFinishReason, hadToolCalls);
        yield return FormatSse("message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens,
                cache_creation_input_tokens = hasCacheCreationInputTokens ? cacheCreationInputTokens : (int?)null,
                cache_read_input_tokens = hasCacheReadInputTokens ? cacheReadInputTokens : (int?)null
            }
        });
        yield return FormatSse("message_stop", new { type = "message_stop" });
    }

    /// <summary>
    /// Non-streaming: deserializes MessagePack body bytes and builds the Anthropic message response.
    /// </summary>
    public static object CollectFromBytes(byte[] bodyBytes, string model)
    {
        var chunks = MessagePackSerializer.Deserialize<List<StreamingChunkDto>>(bodyBytes);
        return CollectFromChunks(chunks, model);
    }

    public static object CollectFromChunks(List<StreamingChunkDto> chunks, string model)
    {
        var messageId = $"msg_{Guid.NewGuid():N}"[..28];
        var contentBuffer = new System.Text.StringBuilder();
        var thinkingBuffer = new System.Text.StringBuilder();
        string? thinkingSignature = null;
        var contentBlocks = new List<object>();
        string? currentToolId = null;
        string? currentToolName = null;
        var currentToolArgs = new System.Text.StringBuilder();
        int inputTokens = 0, outputTokens = 0;
        int cacheCreationInputTokens = 0, cacheReadInputTokens = 0;
        bool hasCacheCreationInputTokens = false, hasCacheReadInputTokens = false;
        string? upstreamFinishReason = null;
        bool hadToolCalls = false;

        foreach (var chunk in chunks)
        {
            if (!string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking:
                    FlushText(contentBlocks, contentBuffer);
                    if (chunk.Text != null) thinkingBuffer.Append(chunk.Text);
                    if (!string.IsNullOrEmpty(chunk.ReasoningSignature))
                        thinkingSignature = chunk.ReasoningSignature;
                    break;

                case StreamingContentType.Text when chunk.Text != null:
                    FlushThinking(contentBlocks, thinkingBuffer, thinkingSignature);
                    thinkingSignature = null;
                    FlushTool(contentBlocks, ref currentToolId, currentToolName, currentToolArgs);
                    contentBuffer.Append(chunk.Text);
                    break;

                case StreamingContentType.FunctionCall:
                    if (chunk.FunctionName != null)
                    {
                        FlushThinking(contentBlocks, thinkingBuffer, thinkingSignature);
                        thinkingSignature = null;
                        FlushText(contentBlocks, contentBuffer);
                        FlushTool(contentBlocks, ref currentToolId, currentToolName, currentToolArgs);
                        currentToolId = chunk.FunctionCallId ?? $"toolu_{Guid.NewGuid():N}";
                        currentToolName = chunk.FunctionName;
                        hadToolCalls = true;
                    }

                    if (chunk.FunctionArguments != null)
                        currentToolArgs.Append(chunk.FunctionArguments);
                    break;

                case StreamingContentType.Usage when chunk.Usage != null:
                    inputTokens = ResolveUsageMonotonic(chunk.Usage, inputTokens, ProviderUsageKeys.InputTokens);
                    outputTokens = ResolveUsageMonotonic(chunk.Usage, outputTokens, ProviderUsageKeys.OutputTokens);
                    if (TryGetUsageInt(chunk.Usage, ProviderUsageKeys.CacheCreationInputTokens, out var cc))
                    {
                        cacheCreationInputTokens = cc;
                        hasCacheCreationInputTokens = true;
                    }

                    if (TryGetUsageInt(chunk.Usage, ProviderUsageKeys.CacheReadInputTokens, out var cr))
                    {
                        cacheReadInputTokens = cr;
                        hasCacheReadInputTokens = true;
                    }
                    break;
            }
        }

        FlushThinking(contentBlocks, thinkingBuffer, thinkingSignature);
        FlushText(contentBlocks, contentBuffer);
        FlushTool(contentBlocks, ref currentToolId, currentToolName, currentToolArgs);

        if (contentBlocks.Count == 0)
            contentBlocks.Add(new { type = "text", text = "" });

        var stopReason = StopReasonMapper.DeriveAnthropicStopReason(upstreamFinishReason, hadToolCalls);

        return new
        {
            id = messageId,
            type = "message",
            role = "assistant",
            content = contentBlocks,
            model,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens,
                cache_creation_input_tokens = hasCacheCreationInputTokens ? cacheCreationInputTokens : (int?)null,
                cache_read_input_tokens = hasCacheReadInputTokens ? cacheReadInputTokens : (int?)null
            }
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CloseBlock(int index)
        => FormatSse("content_block_stop", new { type = "content_block_stop", index });

    /// <summary>
    /// Emits a single <c>signature_delta</c> event for the thinking block being
    /// closed, when the upstream chunk stream carried a signature. No-op for any
    /// other block type or when the signature is missing (Anthropic only sets
    /// the signature on real thinking, not on redacted_thinking which streams
    /// the opaque <c>data</c> as part of its <c>content_block_start</c>).
    /// </summary>
    private static void FlushSignatureIfThinking(string currentBlockType, int blockIndex,
        ref string? pendingSignature, out string? sseEvent)
    {
        sseEvent = null;
        if (currentBlockType != "thinking" || string.IsNullOrEmpty(pendingSignature))
        {
            pendingSignature = null;
            return;
        }
        sseEvent = FormatSse("content_block_delta", new
        {
            type = "content_block_delta",
            index = blockIndex,
            delta = new { type = "signature_delta", signature = pendingSignature }
        });
        pendingSignature = null;
    }

    private static void FlushThinking(List<object> blocks, System.Text.StringBuilder buffer,
        string? signature = null)
    {
        if (buffer.Length <= 0 && string.IsNullOrEmpty(signature)) return;
        var block = new Dictionary<string, object?>
        {
            ["type"] = "thinking",
            ["thinking"] = buffer.ToString()
        };
        if (!string.IsNullOrEmpty(signature)) block["signature"] = signature;
        blocks.Add(block);
        buffer.Clear();
    }

    private static void FlushText(List<object> blocks, System.Text.StringBuilder buffer)
    {
        if (buffer.Length <= 0) return;
        blocks.Add(new { type = "text", text = buffer.ToString() });
        buffer.Clear();
    }

    private static void FlushTool(List<object> blocks, ref string? toolId, string? toolName, System.Text.StringBuilder args)
    {
        if (toolId == null) return;

        object input;
        try
        {
            input = JsonSerializer.Deserialize<JsonElement>(
                args.Length > 0 ? args.ToString() : "{}", JsonSerializerOptions.Default);
        }
        catch { input = new { }; }

        blocks.Add(new { type = "tool_use", id = toolId, name = toolName ?? "", input });
        toolId = null;
        args.Clear();
    }

    private static int ResolveUsageMonotonic(Dictionary<string, long> usage, int current, string key)
    {
        if (!usage.TryGetValue(key, out var raw))
            return current;

        var parsed = (int)Math.Min(raw, int.MaxValue);
        return Math.Max(current, parsed);
    }

    private static bool TryGetUsageInt(Dictionary<string, long> usage, string key, out int value)
    {
        if (usage.TryGetValue(key, out var raw))
        {
            value = (int)Math.Min(raw, int.MaxValue);
            return true;
        }

        value = 0;
        return false;
    }
}
