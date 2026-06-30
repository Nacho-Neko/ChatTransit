using Gateway.Shared.ChatTransit.Mapping;
using Gateway.Shared.Messaging.Serialization;
using MessagePack;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Responses;

/// <summary>
/// Converts <see cref="StreamingChunkDto"/> events into OpenAI Responses API
/// SSE events per the official 2025 spec
/// (https://developers.openai.com/api/docs/guides/streaming-responses?api-mode=responses,
/// https://github.com/openai/openai-python types/responses/).
///
/// <para>Emitted event sequence (one-shot):</para>
/// <code>
/// response.created
/// response.in_progress
/// (for each reasoning block)
///   response.output_item.added       (item type=reasoning)
///   response.reasoning_summary_part.added
///   response.reasoning_summary_text.delta*  (repeating)
///   response.reasoning_summary_text.done
///   response.reasoning_summary_part.done
///   response.output_item.done
/// (for each assistant message block)
///   response.output_item.added       (item type=message)
///   response.content_part.added      (part type=output_text)
///   response.output_text.delta*      (repeating)
///   response.output_text.done
///   response.content_part.done
///   response.output_item.done
/// (for each function call)
///   response.output_item.added       (item type=function_call)
///   response.function_call_arguments.delta*  (repeating)
///   response.function_call_arguments.done
///   response.output_item.done
/// response.completed                 (data contains the full response object incl. output[])
/// </code>
///
/// <para>Every event carries a monotonically-increasing <c>sequence_number</c>
/// and the item/content indices required by the official SDKs to assemble
/// state in order.</para>
/// </summary>
public static class OpenAiResponsesSseEncoder
{
    public static async IAsyncEnumerable<string> StreamAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks, string model,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var responseId = $"resp_{Guid.NewGuid():N}";
        long promptTokens = 0, completionTokens = 0, cachedTokens = 0, reasoningTokens = 0;
        string? upstreamFinishReason = null;

        // Sequence counter — every emitted SSE event carries the next value.
        var seq = 0;
        // Index of the next output item (reasoning / message / function_call).
        var outputIndex = -1;
        // Stable id of the active item (rs_xxx / msg_xxx / fc_xxx).
        string? currentItemId = null;
        // Per-item content/summary state.
        var currentBlockKind = ""; // "reasoning" | "message" | "function_call" | ""
        var summaryIndex = 0;
        var contentIndex = 0;
        var reasoningTextBuffer = new System.Text.StringBuilder();
        // Opaque reasoning signature (Gemini thoughtSignature / Anthropic signature
        // tunnelled through PA). Carried back to the client as the reasoning item's
        // encrypted_content so an OpenAI Responses caller can replay it next turn.
        string? reasoningSignature = null;
        var outputTextBuffer = new System.Text.StringBuilder();
        var functionArgsBuffer = new System.Text.StringBuilder();
        string? currentFunctionName = null;
        string? currentFunctionCallId = null;
        // The full output[] array we build up — used by response.completed.
        var finalOutput = new List<object>();

        string Emit(string eventType, object data)
        {
            // Materialise the chunk now while we still own seq.
            var s = FormatSse(eventType, data);
            seq++;
            return s;
        }

        // ── response.created + response.in_progress ────────────────────────────
        yield return Emit("response.created", new
        {
            type = "response.created",
            sequence_number = seq,
            response = BuildResponseShell(responseId, model, "in_progress", finalOutput, null, null,
                promptTokens, completionTokens, cachedTokens, reasoningTokens)
        });
        yield return Emit("response.in_progress", new
        {
            type = "response.in_progress",
            sequence_number = seq,
            response = BuildResponseShell(responseId, model, "in_progress", finalOutput, null, null,
                promptTokens, completionTokens, cachedTokens, reasoningTokens)
        });

        // Local helpers that close the current open block before opening a new one
        IEnumerable<string> CloseCurrentBlock()
        {
            switch (currentBlockKind)
            {
                case "reasoning":
                {
                    var text = reasoningTextBuffer.ToString();
                    yield return Emit("response.reasoning_summary_text.done", new
                    {
                        type = "response.reasoning_summary_text.done",
                        sequence_number = seq,
                        item_id = currentItemId,
                        output_index = outputIndex,
                        summary_index = summaryIndex,
                        text
                    });
                    yield return Emit("response.reasoning_summary_part.done", new
                    {
                        type = "response.reasoning_summary_part.done",
                        sequence_number = seq,
                        item_id = currentItemId,
                        output_index = outputIndex,
                        summary_index = summaryIndex,
                        part = new { type = "summary_text", text }
                    });
                    var reasoningItem = new Dictionary<string, object?>
                    {
                        ["type"] = "reasoning",
                        ["id"] = currentItemId,
                        ["summary"] = new[] { new { type = "summary_text", text } }
                    };
                    // Preserve the opaque signature so the caller can replay it
                    // (decoded back by OpenAiResponsesInboundDecoder → re-emitted as
                    // thoughtSignature by GeminiOutboundEncoder).
                    if (!string.IsNullOrEmpty(reasoningSignature))
                        reasoningItem["encrypted_content"] = reasoningSignature;
                    finalOutput.Add(reasoningItem);
                    yield return Emit("response.output_item.done", new
                    {
                        type = "response.output_item.done",
                        sequence_number = seq,
                        output_index = outputIndex,
                        item = finalOutput[^1]
                    });
                    reasoningTextBuffer.Clear();
                    reasoningSignature = null;
                    break;
                }
                case "message":
                {
                    var text = outputTextBuffer.ToString();
                    yield return Emit("response.output_text.done", new
                    {
                        type = "response.output_text.done",
                        sequence_number = seq,
                        item_id = currentItemId,
                        output_index = outputIndex,
                        content_index = contentIndex,
                        text
                    });
                    yield return Emit("response.content_part.done", new
                    {
                        type = "response.content_part.done",
                        sequence_number = seq,
                        item_id = currentItemId,
                        output_index = outputIndex,
                        content_index = contentIndex,
                        part = new { type = "output_text", text, annotations = Array.Empty<object>() }
                    });
                    finalOutput.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["id"] = currentItemId,
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new[]
                        {
                            new { type = "output_text", text, annotations = Array.Empty<object>() }
                        }
                    });
                    yield return Emit("response.output_item.done", new
                    {
                        type = "response.output_item.done",
                        sequence_number = seq,
                        output_index = outputIndex,
                        item = finalOutput[^1]
                    });
                    outputTextBuffer.Clear();
                    break;
                }
                case "function_call":
                {
                    var args = functionArgsBuffer.ToString();
                    yield return Emit("response.function_call_arguments.done", new
                    {
                        type = "response.function_call_arguments.done",
                        sequence_number = seq,
                        item_id = currentItemId,
                        output_index = outputIndex,
                        arguments = args
                    });
                    finalOutput.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call",
                        ["id"] = currentItemId,
                        ["call_id"] = currentFunctionCallId,
                        ["name"] = currentFunctionName,
                        ["arguments"] = args,
                        ["status"] = "completed"
                    });
                    yield return Emit("response.output_item.done", new
                    {
                        type = "response.output_item.done",
                        sequence_number = seq,
                        output_index = outputIndex,
                        item = finalOutput[^1]
                    });
                    functionArgsBuffer.Clear();
                    currentFunctionName = null;
                    currentFunctionCallId = null;
                    break;
                }
            }
            currentBlockKind = "";
            currentItemId = null;
        }

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking when chunk.Text != null || !string.IsNullOrEmpty(chunk.ReasoningSignature):
                {
                    if (currentBlockKind != "reasoning")
                    {
                        foreach (var s in CloseCurrentBlock()) yield return s;
                        outputIndex++;
                        currentItemId = $"rs_{Guid.NewGuid():N}";
                        currentBlockKind = "reasoning";
                        summaryIndex = 0;
                        yield return Emit("response.output_item.added", new
                        {
                            type = "response.output_item.added",
                            sequence_number = seq,
                            output_index = outputIndex,
                            item = new
                            {
                                type = "reasoning",
                                id = currentItemId,
                                summary = Array.Empty<object>()
                            }
                        });
                        yield return Emit("response.reasoning_summary_part.added", new
                        {
                            type = "response.reasoning_summary_part.added",
                            sequence_number = seq,
                            item_id = currentItemId,
                            output_index = outputIndex,
                            summary_index = summaryIndex,
                            part = new { type = "summary_text", text = "" }
                        });
                    }
                    // Capture the opaque signature (may arrive on its own chunk with
                    // no text); it is flushed as encrypted_content when the block closes.
                    if (!string.IsNullOrEmpty(chunk.ReasoningSignature))
                        reasoningSignature = chunk.ReasoningSignature;
                    if (chunk.Text != null)
                    {
                        yield return Emit("response.reasoning_summary_text.delta", new
                        {
                            type = "response.reasoning_summary_text.delta",
                            sequence_number = seq,
                            item_id = currentItemId,
                            output_index = outputIndex,
                            summary_index = summaryIndex,
                            delta = chunk.Text
                        });
                        reasoningTextBuffer.Append(chunk.Text);
                    }
                    break;
                }

                case StreamingContentType.Text when chunk.Text != null:
                {
                    if (currentBlockKind != "message")
                    {
                        foreach (var s in CloseCurrentBlock()) yield return s;
                        outputIndex++;
                        currentItemId = $"msg_{Guid.NewGuid():N}";
                        currentBlockKind = "message";
                        contentIndex = 0;
                        yield return Emit("response.output_item.added", new
                        {
                            type = "response.output_item.added",
                            sequence_number = seq,
                            output_index = outputIndex,
                            item = new
                            {
                                type = "message",
                                id = currentItemId,
                                status = "in_progress",
                                role = "assistant",
                                content = Array.Empty<object>()
                            }
                        });
                        yield return Emit("response.content_part.added", new
                        {
                            type = "response.content_part.added",
                            sequence_number = seq,
                            item_id = currentItemId,
                            output_index = outputIndex,
                            content_index = contentIndex,
                            part = new { type = "output_text", text = "", annotations = Array.Empty<object>() }
                        });
                    }
                    yield return Emit("response.output_text.delta", new
                    {
                        type = "response.output_text.delta",
                        sequence_number = seq,
                        item_id = currentItemId,
                        output_index = outputIndex,
                        content_index = contentIndex,
                        delta = chunk.Text
                    });
                    outputTextBuffer.Append(chunk.Text);
                    break;
                }

                case StreamingContentType.FunctionCall:
                {
                    // A new function_call always starts a new item — even when
                    // back-to-back without a message in between, output_index
                    // must increment monotonically (regression #P1-3).
                    if (chunk.FunctionName != null)
                    {
                        foreach (var s in CloseCurrentBlock()) yield return s;
                        outputIndex++;
                        currentItemId = $"fc_{Guid.NewGuid():N}";
                        currentFunctionCallId = chunk.FunctionCallId ?? $"call_{Guid.NewGuid():N}";
                        currentFunctionName = chunk.FunctionName;
                        currentBlockKind = "function_call";
                        yield return Emit("response.output_item.added", new
                        {
                            type = "response.output_item.added",
                            sequence_number = seq,
                            output_index = outputIndex,
                            item = new
                            {
                                type = "function_call",
                                id = currentItemId,
                                call_id = currentFunctionCallId,
                                name = currentFunctionName,
                                arguments = "",
                                status = "in_progress"
                            }
                        });
                    }
                    if (chunk.FunctionArguments != null && currentBlockKind == "function_call")
                    {
                        yield return Emit("response.function_call_arguments.delta", new
                        {
                            type = "response.function_call_arguments.delta",
                            sequence_number = seq,
                            item_id = currentItemId,
                            output_index = outputIndex,
                            delta = chunk.FunctionArguments
                        });
                        functionArgsBuffer.Append(chunk.FunctionArguments);
                    }
                    break;
                }

                case StreamingContentType.Usage when chunk.Usage != null:
                    ReadUsage(chunk.Usage, ref promptTokens, ref completionTokens,
                        ref cachedTokens, ref reasoningTokens);
                    break;

                case StreamingContentType.RawSse when chunk.Text != null:
                    yield return chunk.Text.EndsWith("\n\n", StringComparison.Ordinal)
                        ? chunk.Text
                        : chunk.Text + "\n\n";
                    break;
            }
        }

        // Close the trailing block.
        foreach (var s in CloseCurrentBlock()) yield return s;

        var status = StopReasonMapper.ToIr(upstreamFinishReason) switch
        {
            StopReasonMapper.IrContentFilter => "incomplete",
            StopReasonMapper.IrLength => "incomplete",
            _ => "completed"
        };

        var incompleteReason = status == "incomplete"
            ? StopReasonMapper.ToIr(upstreamFinishReason) switch
            {
                StopReasonMapper.IrLength => "max_output_tokens",
                StopReasonMapper.IrContentFilter => "content_filter",
                _ => StopReasonMapper.ToIr(upstreamFinishReason)
            }
            : null;

        yield return Emit("response.completed", new
        {
            type = "response.completed",
            sequence_number = seq,
            response = BuildResponseShell(responseId, model, status, finalOutput,
                incompleteReason, null,
                promptTokens, completionTokens, cachedTokens, reasoningTokens)
        });
    }

    /// <summary>
    /// Non-streaming: deserializes MessagePack body bytes and collects into a Responses API response.
    /// </summary>
    public static object CollectFromBytes(byte[] bodyBytes, string model)
    {
        var chunks = MessagePackSerializer.Deserialize<List<StreamingChunkDto>>(bodyBytes);
        return CollectFromChunks(chunks, model);
    }

    public static object CollectFromChunks(List<StreamingChunkDto> chunks, string model)
    {
        var responseId = $"resp_{Guid.NewGuid():N}";
        var contentBuffer = new System.Text.StringBuilder();
        var reasoningBuffer = new System.Text.StringBuilder();
        string? reasoningSignature = null;
        var toolCalls = new List<object>();
        string? currentToolItemId = null;
        string? currentToolCallId = null;
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
                case StreamingContentType.Thinking:
                    if (chunk.Text != null)
                        reasoningBuffer.Append(chunk.Text);
                    if (!string.IsNullOrEmpty(chunk.ReasoningSignature))
                        reasoningSignature = chunk.ReasoningSignature;
                    break;
                case StreamingContentType.Text when chunk.Text != null:
                    contentBuffer.Append(chunk.Text);
                    break;
                case StreamingContentType.FunctionCall:
                    if (chunk.FunctionName != null)
                    {
                        FlushTool(toolCalls, ref currentToolItemId, ref currentToolCallId,
                            currentToolName, currentToolArgs);
                        // Responses-API distinguishes:
                        //   id      → server-side item identifier (fc_xxx)
                        //   call_id → reference used by subsequent function_call_output (call_xxx)
                        // FunctionCallId in the IR is the *reference* (call_xxx). The fc_xxx item id
                        // is synthesised here as it isn't carried in the IR stream.
                        currentToolItemId = $"fc_{Guid.NewGuid():N}";
                        currentToolCallId = chunk.FunctionCallId ?? $"call_{Guid.NewGuid():N}";
                        currentToolName = chunk.FunctionName;
                    }
                    if (chunk.FunctionArguments != null)
                        currentToolArgs.Append(chunk.FunctionArguments);
                    break;
                case StreamingContentType.Usage when chunk.Usage != null:
                    ReadUsage(chunk.Usage, ref promptTokens, ref completionTokens,
                        ref cachedTokens, ref reasoningTokens);
                    break;
            }
        }

        FlushTool(toolCalls, ref currentToolItemId, ref currentToolCallId,
            currentToolName, currentToolArgs);

        var output = new List<object>();
        if (reasoningBuffer.Length > 0 || !string.IsNullOrEmpty(reasoningSignature))
        {
            var reasoningItem = new Dictionary<string, object?>
            {
                ["type"] = "reasoning",
                ["summary"] = new[] { new { type = "summary_text", text = reasoningBuffer.ToString() } }
            };
            // Carry the opaque signature back so an OpenAI Responses caller can
            // replay it (round-tripped to thoughtSignature by GeminiOutboundEncoder).
            if (!string.IsNullOrEmpty(reasoningSignature))
                reasoningItem["encrypted_content"] = reasoningSignature;
            output.Add(reasoningItem);
        }
        if (contentBuffer.Length > 0 || toolCalls.Count == 0)
        {
            var outputContent = new List<object>();
            outputContent.Add(new { type = "output_text", text = contentBuffer.ToString() });
            output.Add(new { type = "message", role = "assistant", content = outputContent });
        }
        output.AddRange(toolCalls);

        var inputDetails = cachedTokens > 0 ? (object?)new { cached_tokens = cachedTokens } : null;
        var outputDetails = reasoningTokens > 0 ? (object?)new { reasoning_tokens = reasoningTokens } : null;

        var status = StopReasonMapper.ToIr(upstreamFinishReason) switch
        {
            StopReasonMapper.IrContentFilter => "incomplete",
            StopReasonMapper.IrLength => "incomplete",
            _ => "completed"
        };

        return new
        {
            id = responseId,
            @object = "response",
            status,
            model,
            output,
            incomplete_details = status == "incomplete"
                ? new
                {
                    reason = StopReasonMapper.ToIr(upstreamFinishReason) switch
                    {
                        StopReasonMapper.IrLength => "max_output_tokens",
                        StopReasonMapper.IrContentFilter => "content_filter",
                        _ => StopReasonMapper.ToIr(upstreamFinishReason)
                    }
                }
                : null,
            usage = new
            {
                input_tokens = promptTokens,
                output_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
                input_tokens_details = inputDetails,
                output_tokens_details = outputDetails
            }
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatSse(string eventType, object data)
        => $"event: {eventType}\ndata: {JsonSerializer.Serialize(data)}\n\n";

    private static object BuildResponseShell(string id, string model, string status,
        IReadOnlyList<object> output, string? incompleteReason, string? error,
        long promptTokens, long completionTokens, long cachedTokens, long reasoningTokens)
    {
        var inputDetails = cachedTokens > 0 ? (object?)new { cached_tokens = cachedTokens } : null;
        var outputDetails = reasoningTokens > 0 ? (object?)new { reasoning_tokens = reasoningTokens } : null;

        var shell = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["object"] = "response",
            ["status"] = status,
            ["model"] = model,
            ["output"] = output,
            ["usage"] = new
            {
                input_tokens = promptTokens,
                output_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
                input_tokens_details = inputDetails,
                output_tokens_details = outputDetails
            }
        };
        if (incompleteReason != null)
            shell["incomplete_details"] = new { reason = incompleteReason };
        if (error != null)
            shell["error"] = error;
        return shell;
    }

    private static void FlushTool(List<object> toolCalls,
        ref string? itemId, ref string? callId,
        string? toolName, System.Text.StringBuilder args)
    {
        if (callId == null) return;
        toolCalls.Add(new
        {
            type = "function_call",
            id = itemId ?? $"fc_{Guid.NewGuid():N}",
            call_id = callId,
            name = toolName ?? "",
            arguments = args.ToString(),
            status = "completed"
        });
        itemId = null;
        callId = null;
        args.Clear();
    }

    private static void ReadUsage(Dictionary<string, long> usage,
        ref long prompt, ref long completion, ref long cached, ref long reasoning)
    {
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
