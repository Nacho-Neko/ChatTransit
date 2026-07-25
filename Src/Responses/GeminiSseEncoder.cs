using ChatTransit.Gemini;
using ChatTransit.Mapping;
using MessagePack;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChatTransit.Responses;

/// <summary>
/// Converts <see cref="StreamingChunkDto"/> events into Gemini's
/// <c>generateContent</c> response format.
/// <para>Supports both SSE streaming (<c>?alt=sse</c>) and chunked-JSON array
/// streaming. Maps upstream finish reasons via <see cref="StopReasonMapper"/>
/// and forwards raw SSE chunks for same-protocol passthrough.</para>
/// </summary>
public static class GeminiSseEncoder
{
    /// <summary>
    /// Non-streaming: deserializes MessagePack <c>List&lt;StreamingChunkDto&gt;</c>
    /// and builds the Gemini <c>GenerateContentResponse</c>.
    /// </summary>
    public static GenerateContentResponse CollectFromBytes(byte[] bodyBytes)
    {
        if (bodyBytes.Length == 0)
        {
            return new GenerateContentResponse
            {
                Candidates =
                [
                    new Candidate
                    {
                        Content = new GeminiContent { Role = "model", Parts = [new GeminiPart { Text = "" }] },
                        FinishReason = "STOP",
                        Index = 0
                    }
                ],
                UsageMetadata = BuildUsageMetadata(0, 0, 0)
            };
        }

        var chunks = MessagePackSerializer.Deserialize<List<StreamingChunkDto>>(bodyBytes);
        return CollectFromChunks(chunks);
    }

    public static GenerateContentResponse CollectFromChunks(List<StreamingChunkDto> chunks)
    {
        var contentBuffer = new System.Text.StringBuilder();
        var thinkingBuffer = new System.Text.StringBuilder();
        string? thinkingSignature = null;
        string? contentSignature = null;
        var toolCalls = new List<GeminiPart>();
        string? currentToolName = null;
        string? currentToolId = null;
        string? currentToolSignature = null;
        var currentToolArgs = new System.Text.StringBuilder();
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var reasoningTokens = 0;
        string? upstreamFinishReason = null;
        bool hadToolCalls = false;

        foreach (var chunk in chunks)
        {
            if (!IsThoughtChunk(chunk) && !string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking:
                    if (chunk.Text != null)
                        thinkingBuffer.Append(chunk.Text);
                    // Capture the opaque thoughtSignature so the aggregated thought
                    // part can echo it back (the merged single-block shape mirrors
                    // the existing thinking-text aggregation). Last non-empty wins.
                    if (!string.IsNullOrEmpty(chunk.ReasoningSignature))
                        thinkingSignature = chunk.ReasoningSignature;
                    break;

                case StreamingContentType.Text when chunk.Text != null:
                    if (IsThoughtChunk(chunk))
                    {
                        thinkingBuffer.Append(chunk.Text);
                        if (!string.IsNullOrEmpty(chunk.ReasoningSignature))
                            thinkingSignature = chunk.ReasoningSignature;
                    }
                    else
                    {
                        contentBuffer.Append(chunk.Text);
                        // A non-thought text part may carry a thoughtSignature in
                        // Gemini 3; keep the last non-empty one for the merged part.
                        if (!string.IsNullOrEmpty(chunk.ReasoningSignature))
                            contentSignature = chunk.ReasoningSignature;
                    }
                    break;

                case StreamingContentType.FunctionCall:
                    if (chunk.FunctionName != null)
                    {
                        FlushToolCall(toolCalls, ref currentToolName, ref currentToolId, ref currentToolSignature, currentToolArgs);
                        currentToolName = chunk.FunctionName;
                        currentToolId = chunk.FunctionCallId;
                        currentToolSignature = chunk.ReasoningSignature;
                        hadToolCalls = true;
                    }
                    if (chunk.FunctionArguments != null)
                        currentToolArgs.Append(chunk.FunctionArguments);
                    break;

                case StreamingContentType.Usage when chunk.Usage != null:
                    promptTokens = ResolveUsageLong(chunk.Usage, promptTokens, ChatTransitUsageKeys.InputCandidates);
                    completionTokens = ResolveUsageLong(chunk.Usage, completionTokens, ChatTransitUsageKeys.OutputCandidates);
                    cachedTokens = ResolveUsageLong(chunk.Usage, cachedTokens, ChatTransitUsageKeys.CacheReadInputCandidates);
                    reasoningTokens = ResolveUsageLong(chunk.Usage, reasoningTokens, ReasoningCandidates);
                    break;
            }
        }

        FlushToolCall(toolCalls, ref currentToolName, ref currentToolId, ref currentToolSignature, currentToolArgs);

        var parts = new List<GeminiPart>();
        if (thinkingBuffer.Length > 0 || !string.IsNullOrEmpty(thinkingSignature))
            parts.Add(new GeminiPart
            {
                Text = thinkingBuffer.Length > 0 ? thinkingBuffer.ToString() : null,
                Thought = true,
                ThoughtSignature = thinkingSignature,
            });
        if (contentBuffer.Length > 0)
            parts.Add(new GeminiPart
            {
                Text = contentBuffer.ToString(),
                ThoughtSignature = string.IsNullOrEmpty(contentSignature) ? null : contentSignature,
            });
        parts.AddRange(toolCalls);
        if (parts.Count == 0)
            parts.Add(new GeminiPart { Text = "" });

        var finishReason = StopReasonMapper.DeriveGeminiFinishReason(upstreamFinishReason, hadToolCalls);

        return new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new GeminiContent { Role = "model", Parts = parts },
                    FinishReason = finishReason,
                    Index = 0
                }
            ],
            UsageMetadata = BuildUsageMetadata(promptTokens, completionTokens, cachedTokens, reasoningTokens)
        };
    }

    /// <summary>
    /// SSE streaming: emits <c>data: {chunk_json}\n\n</c> per <c>?alt=sse</c> mode.
    /// </summary>
    public static async IAsyncEnumerable<string> StreamSseAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in BuildChunks(chunks, ct))
        {
            // Raw SSE passthrough (same-protocol fast path) — already framed
            if (item.Raw is string raw)
            {
                yield return raw.EndsWith("\n\n", StringComparison.Ordinal) ? raw : raw + "\n\n";
                continue;
            }
            yield return $"data: {JsonSerializer.Serialize(item.Response)}\n\n";
        }
    }

    /// <summary>
    /// Chunked-JSON-array streaming (default Gemini transport without ?alt=sse).
    /// Output: <c>[{chunk}\n,{chunk}\n,...\n]</c>.
    /// </summary>
    public static async IAsyncEnumerable<string> StreamChunkedJsonAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var first = true;
        yield return "[";
        await foreach (var item in BuildChunks(chunks, ct))
        {
            if (item.Raw is string) continue; // raw SSE makes no sense in chunked-json mode
            if (!first) yield return "\n,";
            yield return JsonSerializer.Serialize(item.Response);
            first = false;
        }
        yield return "\n]";
    }

    private readonly struct StreamItem
    {
        public GenerateContentResponse? Response { get; init; }
        public string? Raw { get; init; }
    }

    private static async IAsyncEnumerable<StreamItem> BuildChunks(
        IAsyncEnumerable<StreamingChunkDto> chunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var reasoningTokens = 0;
        string? currentToolName = null;
        string? currentToolId = null;
        string? currentToolSignature = null;
        var currentToolArgs = new System.Text.StringBuilder();
        string? upstreamFinishReason = null;
        bool hadToolCalls = false;

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (!IsThoughtChunk(chunk) && !string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking when chunk.Text != null || !string.IsNullOrEmpty(chunk.ReasoningSignature):
                    // Echo the opaque thoughtSignature back so a Gemini-format
                    // caller can replay it next turn (Gemini 3 thinking continuity;
                    // Claude-via-PA "thinking.signature: Field required"). A
                    // signature-only chunk (no text) still emits a thought part so
                    // the signature is not lost.
                    yield return new StreamItem
                    {
                        Response = MakeChunk(new GeminiPart
                        {
                            Text = chunk.Text,
                            Thought = true,
                            ThoughtSignature = chunk.ReasoningSignature,
                        })
                    };
                    break;

                case StreamingContentType.Text when chunk.Text != null:
                    // A thoughtSignature can ride on an ordinary (non-thought) text
                    // part in Gemini 3; echo it back on the emitted text part so the
                    // caller can replay it next turn.
                    yield return new StreamItem
                    {
                        Response = MakeChunk(new GeminiPart
                        {
                            Text = chunk.Text,
                            Thought = IsThoughtChunk(chunk),
                            ThoughtSignature = string.IsNullOrEmpty(chunk.ReasoningSignature) ? null : chunk.ReasoningSignature,
                        })
                    };
                    break;

                case StreamingContentType.FunctionCall:
                    if (chunk.FunctionName != null)
                    {
                        if (currentToolName != null)
                        {
                            var parsedPrev = TryParseArgs(currentToolArgs);
                            yield return new StreamItem
                            {
                                Response = MakeChunk(BuildFunctionCallPart(
                                    currentToolName, currentToolId, parsedPrev, currentToolSignature), finishReason: null)
                            };
                            currentToolArgs.Clear();
                        }
                        currentToolName = chunk.FunctionName;
                        currentToolId = chunk.FunctionCallId;
                        currentToolSignature = chunk.ReasoningSignature;
                        hadToolCalls = true;
                    }
                    if (chunk.FunctionArguments != null)
                        currentToolArgs.Append(chunk.FunctionArguments);
                    break;

                case StreamingContentType.Usage when chunk.Usage != null:
                    promptTokens = ResolveUsageLong(chunk.Usage, promptTokens, ChatTransitUsageKeys.InputCandidates);
                    completionTokens = ResolveUsageLong(chunk.Usage, completionTokens, ChatTransitUsageKeys.OutputCandidates);
                    cachedTokens = ResolveUsageLong(chunk.Usage, cachedTokens, ChatTransitUsageKeys.CacheReadInputCandidates);
                    reasoningTokens = ResolveUsageLong(chunk.Usage, reasoningTokens, ReasoningCandidates);
                    break;

                case StreamingContentType.RawSse when chunk.Text != null:
                    yield return new StreamItem { Raw = chunk.Text };
                    break;
            }
        }

        if (currentToolName != null)
        {
            var parsedArgs = TryParseArgs(currentToolArgs);
            yield return new StreamItem
            {
                Response = MakeChunk(BuildFunctionCallPart(currentToolName, currentToolId, parsedArgs, currentToolSignature))
            };
        }

        var finishReason = StopReasonMapper.DeriveGeminiFinishReason(upstreamFinishReason, hadToolCalls);
        yield return new StreamItem
        {
            Response = BuildFinalResponse([], promptTokens, completionTokens, cachedTokens, reasoningTokens, finishReason)
        };
    }

    // Gemini's functionCall.args is documented to always be an object; a null
    // (empty or unparseable args) must serialize as {} rather than JSON null.
    private static readonly JsonElement EmptyArgsObject =
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

    private static GeminiPart BuildFunctionCallPart(string name, string? id, JsonElement? args, string? signature = null)
    {
        var part = new GeminiPart
        {
            FunctionCall = new FunctionCall { Name = name, Args = args ?? EmptyArgsObject },
            // Gemini 3 requires the thoughtSignature to be echoed back on the exact
            // functionCall part it was received on, or the next turn 400s.
            ThoughtSignature = string.IsNullOrEmpty(signature) ? null : signature,
        };
        if (!string.IsNullOrEmpty(id) && !string.Equals(id, name, StringComparison.Ordinal))
            part.FunctionCall.Id = id;
        return part;
    }

    private static GenerateContentResponse BuildFinalResponse(
        List<GeminiPart> parts,
        int promptTokens,
        int completionTokens,
        int cachedTokens,
        int reasoningTokens,
        string finishReason)
    {
        var candidate = new Candidate
        {
            FinishReason = finishReason,
            Index = 0
        };
        // The closing chunk carries only finishReason + usage; emitting a content
        // object with an empty parts[] is non-conformant, so omit content entirely
        // when there is nothing left to send.
        if (parts.Count > 0)
            candidate.Content = new GeminiContent { Role = "model", Parts = parts };

        return new GenerateContentResponse
        {
            Candidates = [candidate],
            UsageMetadata = BuildUsageMetadata(promptTokens, completionTokens, cachedTokens, reasoningTokens)
        };
    }

    private static GenerateContentResponse MakeChunk(GeminiPart part, string? finishReason = null) => new()
    {
        Candidates =
        [
            new Candidate
            {
                Content = new GeminiContent { Role = "model", Parts = [part] },
                FinishReason = finishReason,
                Index = 0
            }
        ]
    };

    private static UsageMetadata BuildUsageMetadata(int prompt, int completion, int cached, int reasoning = 0) => new()
    {
        PromptTokenCount = prompt,
        CandidatesTokenCount = completion,
        // Per the Gemini API, totalTokenCount counts prompt + output candidates +
        // thoughts (thoughtsTokenCount is separate from candidatesTokenCount).
        TotalTokenCount = prompt + completion + reasoning,
        CachedContentTokenCount = cached > 0 ? cached : null,
        ThoughtsTokenCount = reasoning > 0 ? reasoning : null
    };

    private static void FlushToolCall(
        List<GeminiPart> parts, ref string? toolName, ref string? toolId,
        ref string? toolSignature, System.Text.StringBuilder args)
    {
        if (toolName == null) return;

        JsonElement? parsedArgs = TryParseArgs(args);
        parts.Add(BuildFunctionCallPart(toolName, toolId, parsedArgs, toolSignature));

        toolName = null;
        toolId = null;
        toolSignature = null;
        args.Clear();
    }

    private static JsonElement? TryParseArgs(System.Text.StringBuilder args)
    {
        if (args.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(args.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static bool IsThoughtChunk(StreamingChunkDto chunk)
        => string.Equals(chunk.FinishReason, "thought", StringComparison.OrdinalIgnoreCase)
           || string.Equals(chunk.AuthorRole, "thought", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ReasoningCandidates =
        [ChatTransitUsageKeys.ReasoningTokens, "reasoning_tokens", "thoughtsTokenCount"];

    private static int ResolveUsageLong(Dictionary<string, long> usage, int current, string[] keys)
    {
        foreach (var key in keys)
        {
            if (usage.TryGetValue(key, out var raw))
                return (int)Math.Min(raw, int.MaxValue);
        }
        return current;
    }
}
