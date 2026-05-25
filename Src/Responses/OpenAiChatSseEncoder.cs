using Gateway.Shared.ChatTransit.Mapping;
using Gateway.Shared.Messaging.Serialization;
using MessagePack;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Responses;

/// <summary>
/// Converts <see cref="StreamingChunkDto"/> events into OpenAI Chat Completions
/// SSE chunks and the non-streaming aggregated <c>chat.completion</c> body.
/// <para>Maps upstream <c>finish_reason</c> via <see cref="StopReasonMapper"/>,
/// emits <c>reasoning_content</c> deltas, honors <c>stream_options.include_usage</c>,
/// and forwards raw SSE chunks for same-protocol passthrough.</para>
/// </summary>
public static class OpenAiChatSseEncoder
{
    public static async IAsyncEnumerable<string> StreamAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks, string model,
        bool includeUsage = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fingerprint = $"fp_{Guid.NewGuid():N}"[..16];
        var toolCallIndex = -1;
        long promptTokens = 0, completionTokens = 0, cachedTokens = 0;
        long reasoningTokens = 0;
        string? upstreamFinishReason = null;
        var firstDelta = true;

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking when chunk.Text != null:
                    {
                        yield return FormatChunk(completionId, created, model, fingerprint,
                            BuildDelta(firstDelta, reasoningContent: chunk.Text),
                            finishReason: null);
                        firstDelta = false;
                        break;
                    }

                case StreamingContentType.Text when chunk.Text != null:
                    {
                        yield return FormatChunk(completionId, created, model, fingerprint,
                            BuildDelta(firstDelta, content: chunk.Text),
                            finishReason: null);
                        firstDelta = false;
                        break;
                    }

                case StreamingContentType.FunctionCall:
                    {
                        if (chunk.FunctionName != null)
                        {
                            toolCallIndex++;
                            var tc = new[]
                            {
                                new
                                {
                                    index = toolCallIndex,
                                    id = chunk.FunctionCallId ?? $"call_{Guid.NewGuid():N}",
                                    type = "function",
                                    function = new { name = chunk.FunctionName, arguments = chunk.FunctionArguments ?? "" }
                                }
                            };
                            yield return FormatChunk(completionId, created, model, fingerprint,
                                BuildDelta(firstDelta, toolCalls: tc),
                                finishReason: null);
                            firstDelta = false;
                        }
                        else if (chunk.FunctionArguments != null)
                        {
                            var tc = new[]
                            {
                                new
                                {
                                    index = toolCallIndex,
                                    id = (string?)null,
                                    type = (string?)null,
                                    function = new { name = (string?)null, arguments = chunk.FunctionArguments }
                                }
                            };
                            yield return FormatChunk(completionId, created, model, fingerprint,
                                BuildDelta(firstDelta: false, toolCalls: tc),
                                finishReason: null);
                        }
                        break;
                    }

                case StreamingContentType.Usage when chunk.Usage != null:
                    {
                        ReadUsage(chunk.Usage, ref promptTokens, ref completionTokens, ref cachedTokens, ref reasoningTokens);
                        break;
                    }

                case StreamingContentType.RawSse when chunk.Text != null:
                    yield return chunk.Text.EndsWith("\n\n", StringComparison.Ordinal)
                        ? chunk.Text
                        : chunk.Text + "\n\n";
                    break;
            }
        }

        var finishReason = StopReasonMapper.DeriveOpenAiFinishReason(upstreamFinishReason, toolCallIndex >= 0);
        yield return FormatChunk(completionId, created, model, fingerprint, new { }, finishReason: finishReason);

        if (includeUsage)
        {
            var promptDetails = cachedTokens > 0
                ? (object?)new { cached_tokens = cachedTokens }
                : null;
            var completionDetails = reasoningTokens > 0
                ? (object?)new { reasoning_tokens = reasoningTokens }
                : null;
            var usageChunk = new
            {
                id = completionId,
                @object = "chat.completion.chunk",
                created,
                model,
                system_fingerprint = fingerprint,
                choices = Array.Empty<object>(),
                usage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = completionTokens,
                    total_tokens = promptTokens + completionTokens,
                    prompt_tokens_details = promptDetails,
                    completion_tokens_details = completionDetails
                }
            };
            yield return $"data: {JsonSerializer.Serialize(usageChunk)}\n\n";
        }

        yield return "data: [DONE]\n\n";
    }

    /// <summary>
    /// Non-streaming: deserializes MessagePack body bytes into a chunk list,
    /// then collects into a full <c>chat.completion</c> response.
    /// </summary>
    public static object CollectFromBytes(byte[] bodyBytes, string model)
    {
        var chunks = MessagePackSerializer.Deserialize<List<StreamingChunkDto>>(bodyBytes);
        return CollectFromChunks(chunks, model);
    }

    public static object CollectFromChunks(List<StreamingChunkDto> chunks, string model)
    {
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fingerprint = $"fp_{Guid.NewGuid():N}"[..16];
        var contentBuffer = new System.Text.StringBuilder();
        var reasoningBuffer = new System.Text.StringBuilder();
        var toolCalls = new List<object>();
        string? currentToolId = null;
        string? currentToolName = null;
        var currentToolArgs = new System.Text.StringBuilder();
        long promptTokens = 0, completionTokens = 0, cachedTokens = 0, reasoningTokens = 0;
        string? upstreamFinishReason = null;

        foreach (var chunk in chunks)
        {
            if (!string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking when chunk.Text != null:
                    reasoningBuffer.Append(chunk.Text);
                    break;

                case StreamingContentType.Text when chunk.Text != null:
                    contentBuffer.Append(chunk.Text);
                    break;

                case StreamingContentType.FunctionCall:
                    if (chunk.FunctionName != null)
                    {
                        FlushTool(toolCalls, ref currentToolId, currentToolName, currentToolArgs);
                        currentToolId = chunk.FunctionCallId ?? $"call_{Guid.NewGuid():N}";
                        currentToolName = chunk.FunctionName;
                    }
                    if (chunk.FunctionArguments != null)
                        currentToolArgs.Append(chunk.FunctionArguments);
                    break;

                case StreamingContentType.Usage when chunk.Usage != null:
                    ReadUsage(chunk.Usage, ref promptTokens, ref completionTokens, ref cachedTokens, ref reasoningTokens);
                    break;
            }
        }

        FlushTool(toolCalls, ref currentToolId, currentToolName, currentToolArgs);

        var finishReason = StopReasonMapper.DeriveOpenAiFinishReason(upstreamFinishReason, toolCalls.Count > 0);
        var reasoning = reasoningBuffer.Length > 0 ? reasoningBuffer.ToString() : (string?)null;
        var message = toolCalls.Count > 0
            ? (object)new
            {
                role = "assistant",
                content = contentBuffer.Length > 0 ? contentBuffer.ToString() : (string?)null,
                reasoning_content = reasoning,
                tool_calls = toolCalls
            }
            : new
            {
                role = "assistant",
                content = contentBuffer.ToString(),
                reasoning_content = reasoning,
                tool_calls = (List<object>?)null
            };

        var promptDetails = cachedTokens > 0
            ? (object?)new { cached_tokens = cachedTokens }
            : null;
        var completionDetails = reasoningTokens > 0
            ? (object?)new { reasoning_tokens = reasoningTokens }
            : null;

        return new
        {
            id = completionId,
            @object = "chat.completion",
            created,
            model,
            system_fingerprint = fingerprint,
            choices = new[] { new { index = 0, message, finish_reason = finishReason } },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
                prompt_tokens_details = promptDetails,
                completion_tokens_details = completionDetails
            }
        };
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static object BuildDelta(bool firstDelta,
        string? content = null, string? reasoningContent = null, object? toolCalls = null)
    {
        // Per the official spec, delta SHOULD omit fields that have no value
        // (rather than emitting them as null). Some SDKs treat `"content": null`
        // as a signal that no more text deltas are coming — emitting it on
        // every reasoning/tool_call chunk confuses them.
        var delta = new Dictionary<string, object?>(StringComparer.Ordinal);
        // First delta needs a "role":"assistant"; subsequent ones don't.
        if (firstDelta) delta["role"] = "assistant";
        if (content != null) delta["content"] = content;
        if (reasoningContent != null) delta["reasoning_content"] = reasoningContent;
        if (toolCalls != null) delta["tool_calls"] = toolCalls;
        return delta;
    }

    private static string FormatChunk(string id, long created, string model, string fingerprint,
        object delta, string? finishReason)
    {
        var chunk = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            system_fingerprint = fingerprint,
            choices = new[] { new { index = 0, delta, finish_reason = finishReason } }
        };
        return $"data: {JsonSerializer.Serialize(chunk)}\n\n";
    }

    private static void FlushTool(List<object> toolCalls, ref string? currentToolId,
        string? currentToolName, System.Text.StringBuilder args)
    {
        if (currentToolId == null) return;
        toolCalls.Add(new
        {
            id = currentToolId,
            type = "function",
            function = new { name = currentToolName, arguments = args.ToString() }
        });
        currentToolId = null;
        args.Clear();
    }

    private static void ReadUsage(Dictionary<string, long> usage,
        ref long prompt, ref long completion, ref long cached, ref long reasoning)
    {
        // Prefer canonical keys; tolerate legacy aliases.
        foreach (var k in new[] { "inputTokens", "input_tokens", "prompt_tokens", "promptTokens" })
            if (usage.TryGetValue(k, out var v)) { prompt = Math.Max(prompt, v); break; }
        foreach (var k in new[] { "outputTokens", "output_tokens", "completion_tokens", "completionTokens" })
            if (usage.TryGetValue(k, out var v)) { completion = Math.Max(completion, v); break; }
        foreach (var k in new[] { "cacheReadInputTokens", "cache_read_input_tokens" })
            if (usage.TryGetValue(k, out var v)) { cached = Math.Max(cached, v); break; }
        foreach (var k in new[] { "reasoningTokens", "reasoning_tokens" })
            if (usage.TryGetValue(k, out var v)) { reasoning = Math.Max(reasoning, v); break; }
    }
}
