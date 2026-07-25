using ChatTransit.Mapping;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatTransit.Responses;

/// <summary>
/// Parses the OpenAI Responses API's own SSE event stream (and non-streaming
/// <c>response</c> body) back into canonical <see cref="StreamingChunkDto"/>
/// values. Inverse of <see cref="OpenAiResponsesSseEncoder"/> — only the events
/// that carry content a cross-protocol re-render needs (deltas, item-added for
/// function-call identity, item-done for the reasoning signature, and the final
/// usage/status) are consumed; the rest of the elaborate event sequence exists
/// purely for the official SDKs' incremental UI state and has no neutral-chunk
/// equivalent.
/// </summary>
public static class OpenAiResponsesSseDecoder
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
                    var chunk = DecodeEvent(doc.RootElement);
                    if (chunk != null) yield return chunk;
                }
            }
        }
    }

    /// <summary>
    /// Non-streaming: parses a full Responses API <c>response</c> JSON body into chunks.
    /// </summary>
    public static List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
    {
        var chunks = new List<StreamingChunkDto>();
        using var doc = JsonDocument.Parse(nativeJsonBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputEl.EnumerateArray())
            {
                var type = UsageDictBuilder.GetString(item, "type");
                switch (type)
                {
                    case "reasoning":
                    {
                        // Newline-join the summary parts (consistent with the inbound
                        // decoder) and also fold in the raw reasoning `content` parts.
                        var sb = new System.Text.StringBuilder();
                        if (item.TryGetProperty("summary", out var summaryEl) && summaryEl.ValueKind == JsonValueKind.Array)
                            foreach (var s in summaryEl.EnumerateArray())
                                if (UsageDictBuilder.GetString(s, "text") is { Length: > 0 } st)
                                {
                                    if (sb.Length > 0) sb.Append('\n');
                                    sb.Append(st);
                                }
                        if (item.TryGetProperty("content", out var rcEl) && rcEl.ValueKind == JsonValueKind.Array)
                            foreach (var s in rcEl.EnumerateArray())
                                if (UsageDictBuilder.GetString(s, "text") is { Length: > 0 } ct)
                                {
                                    if (sb.Length > 0) sb.Append('\n');
                                    sb.Append(ct);
                                }
                        var text = sb.ToString();
                        var signature = UsageDictBuilder.GetString(item, "encrypted_content");
                        if (text.Length > 0 || signature != null)
                            chunks.Add(new StreamingChunkDto
                            {
                                ContentType = StreamingContentType.Thinking,
                                Text = text.Length > 0 ? text : null,
                                ReasoningSignature = signature,
                            });
                        break;
                    }

                    case "message":
                    {
                        if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in contentEl.EnumerateArray())
                            {
                                var text = UsageDictBuilder.GetString(part, "text");
                                if (text != null)
                                    chunks.Add(new StreamingChunkDto { ContentType = StreamingContentType.Text, Text = text });
                                // A refusal part carries its text under "refusal", not
                                // "text" — surface it as a Text chunk so it isn't lost.
                                else if (UsageDictBuilder.GetString(part, "type") == "refusal"
                                         && UsageDictBuilder.GetString(part, "refusal") is { } refusal)
                                    chunks.Add(new StreamingChunkDto { ContentType = StreamingContentType.Text, Text = refusal });
                            }
                        }
                        break;
                    }

                    case "function_call":
                        chunks.Add(new StreamingChunkDto
                        {
                            ContentType = StreamingContentType.FunctionCall,
                            FunctionName = UsageDictBuilder.GetString(item, "name"),
                            FunctionCallId = UsageDictBuilder.GetString(item, "call_id"),
                            FunctionArguments = UsageDictBuilder.GetString(item, "arguments") ?? "{}",
                        });
                        break;
                }
            }
        }

        var finishReason = ResolveFinishReason(root);
        Dictionary<string, long>? usage = root.TryGetProperty("usage", out var usageEl) ? ReadResponsesUsage(usageEl) : null;
        var (err, errCode) = ReadResponseError(root);
        if (finishReason != null || usage != null || err != null)
            chunks.Add(new StreamingChunkDto
            {
                ContentType = StreamingContentType.Usage,
                FinishReason = finishReason,
                Usage = usage,
                Error = err,
                ErrorCode = errCode,
            });

        return chunks;
    }

    private static StreamingChunkDto? DecodeEvent(JsonElement root)
    {
        var type = UsageDictBuilder.GetString(root, "type");
        switch (type)
        {
            case "response.output_item.added":
            {
                if (!root.TryGetProperty("item", out var item)
                    || UsageDictBuilder.GetString(item, "type") != "function_call")
                    return null;
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.FunctionCall,
                    FunctionName = UsageDictBuilder.GetString(item, "name"),
                    FunctionCallId = UsageDictBuilder.GetString(item, "call_id"),
                };
            }

            case "response.output_text.delta":
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Text,
                    Text = UsageDictBuilder.GetString(root, "delta"),
                };

            case "response.refusal.delta":
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Text,
                    Text = UsageDictBuilder.GetString(root, "delta"),
                };

            case "response.reasoning_summary_text.delta":
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Thinking,
                    Text = UsageDictBuilder.GetString(root, "delta"),
                };

            case "response.function_call_arguments.delta":
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.FunctionCall,
                    FunctionArguments = UsageDictBuilder.GetString(root, "delta"),
                };

            case "response.output_item.done":
            {
                if (!root.TryGetProperty("item", out var item)
                    || UsageDictBuilder.GetString(item, "type") != "reasoning")
                    return null;
                var signature = UsageDictBuilder.GetString(item, "encrypted_content");
                return signature == null ? null : new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Thinking,
                    ReasoningSignature = signature,
                };
            }

            // All three are terminal envelopes. "incomplete" carries the real stop
            // reason (max_output_tokens/content_filter) and usage — dropping it made
            // truncated turns look like a clean "stop" and zeroed out the usage for
            // billing. "failed" carries an error object. Handling them here stops the
            // upstream's truncation/failure from being silently rendered as success.
            case "response.completed":
            case "response.incomplete":
            case "response.failed":
            {
                if (!root.TryGetProperty("response", out var response)) return null;
                var finishReason = ResolveFinishReason(response);
                var usage = response.TryGetProperty("usage", out var usageEl) ? ReadResponsesUsage(usageEl) : null;
                var (err, errCode) = ReadResponseError(response);
                if (finishReason == null && usage == null && err == null) return null;
                return new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Usage,
                    FinishReason = finishReason,
                    Usage = usage,
                    Error = err,
                    ErrorCode = errCode,
                };
            }

            // Standalone stream error event (not wrapped in a response object).
            case "error":
            {
                var (err, errCode) = ReadResponseError(root);
                err ??= UsageDictBuilder.GetString(root, "message");
                return err == null ? null : new StreamingChunkDto
                {
                    ContentType = StreamingContentType.Usage,
                    Error = err,
                    ErrorCode = errCode,
                };
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The Responses API only surfaces an explicit stop reason for the "incomplete"
    /// terminal states; a normal completion (incl. one ending in tool calls) has no
    /// native finish-reason string at all, so returning null here is correct — the
    /// consuming encoder's fallback (derived from whether any FunctionCall chunk was
    /// seen) already produces "tool_calls"/"stop" without one.
    /// </summary>
    private static string? ResolveFinishReason(JsonElement response)
    {
        if (UsageDictBuilder.GetString(response, "status") != "incomplete") return null;
        if (!response.TryGetProperty("incomplete_details", out var details)) return null;
        return UsageDictBuilder.GetString(details, "reason") switch
        {
            "max_output_tokens" => "length",
            "content_filter" => "content_filter",
            var other => other,
        };
    }

    /// <summary>
    /// Extracts (message, code) from either a <c>response.error</c> object or a
    /// standalone <c>error</c> event whose fields sit directly on the root.
    /// </summary>
    private static (string? Message, string? Code) ReadResponseError(JsonElement obj)
    {
        if (obj.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            return (UsageDictBuilder.GetString(e, "message"), UsageDictBuilder.GetString(e, "code"));
        return (UsageDictBuilder.GetString(obj, "message"), UsageDictBuilder.GetString(obj, "code"));
    }

    private static Dictionary<string, long>? ReadResponsesUsage(JsonElement usage)
    {
        var inputDetails = usage.TryGetProperty("input_tokens_details", out var itd) ? itd : default;
        var outputDetails = usage.TryGetProperty("output_tokens_details", out var otd) ? otd : default;
        return UsageDictBuilder.Build(
            inputTokens: UsageDictBuilder.GetLong(usage, "input_tokens"),
            outputTokens: UsageDictBuilder.GetLong(usage, "output_tokens"),
            cacheReadInputTokens: inputDetails.ValueKind == JsonValueKind.Object
                ? UsageDictBuilder.GetLong(inputDetails, "cached_tokens") : null,
            reasoningTokens: outputDetails.ValueKind == JsonValueKind.Object
                ? UsageDictBuilder.GetLong(outputDetails, "reasoning_tokens") : null);
    }
}
