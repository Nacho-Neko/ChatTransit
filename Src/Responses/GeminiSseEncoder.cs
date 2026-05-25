using Gateway.Shared.ChatTransit.Gemini;
using Gateway.Shared.ChatTransit.Mapping;
using Gateway.Shared.Messaging.Serialization;
using Gateway.Shared.Providers.Streaming;
using MessagePack;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Responses;

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
        var toolCalls = new List<GeminiPart>();
        string? currentToolName = null;
        string? currentToolId = null;
        var currentToolArgs = new System.Text.StringBuilder();
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        string? upstreamFinishReason = null;
        bool hadToolCalls = false;

        foreach (var chunk in chunks)
        {
            if (!IsThoughtChunk(chunk) && !string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking when chunk.Text != null:
                    thinkingBuffer.Append(chunk.Text);
                    break;

                case StreamingContentType.Text when chunk.Text != null:
                    if (IsThoughtChunk(chunk))
                        thinkingBuffer.Append(chunk.Text);
                    else
                        contentBuffer.Append(chunk.Text);
                    break;

                case StreamingContentType.FunctionCall:
                    if (chunk.FunctionName != null)
                    {
                        FlushToolCall(toolCalls, ref currentToolName, ref currentToolId, currentToolArgs);
                        currentToolName = chunk.FunctionName;
                        currentToolId = chunk.FunctionCallId;
                        hadToolCalls = true;
                    }
                    if (chunk.FunctionArguments != null)
                        currentToolArgs.Append(chunk.FunctionArguments);
                    break;

                case StreamingContentType.Usage when chunk.Usage != null:
                    promptTokens = ResolveUsageLong(chunk.Usage, promptTokens, ProviderUsageKeys.InputCandidates);
                    completionTokens = ResolveUsageLong(chunk.Usage, completionTokens, ProviderUsageKeys.OutputCandidates);
                    cachedTokens = ResolveUsageLong(chunk.Usage, cachedTokens, ProviderUsageKeys.CacheReadInputCandidates);
                    break;
            }
        }

        FlushToolCall(toolCalls, ref currentToolName, ref currentToolId, currentToolArgs);

        var parts = new List<GeminiPart>();
        if (thinkingBuffer.Length > 0)
            parts.Add(new GeminiPart { Text = thinkingBuffer.ToString(), Thought = true });
        if (contentBuffer.Length > 0)
            parts.Add(new GeminiPart { Text = contentBuffer.ToString() });
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
            UsageMetadata = BuildUsageMetadata(promptTokens, completionTokens, cachedTokens)
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
        string? currentToolName = null;
        string? currentToolId = null;
        var currentToolArgs = new System.Text.StringBuilder();
        var hasOutput = false;
        string? upstreamFinishReason = null;
        bool hadToolCalls = false;

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (!IsThoughtChunk(chunk) && !string.IsNullOrEmpty(chunk.FinishReason))
                upstreamFinishReason = chunk.FinishReason;

            switch (chunk.ContentType)
            {
                case StreamingContentType.Thinking when chunk.Text != null:
                    hasOutput = true;
                    yield return new StreamItem
                    {
                        Response = MakeChunk(new GeminiPart { Text = chunk.Text, Thought = true })
                    };
                    break;

                case StreamingContentType.Text when chunk.Text != null:
                    hasOutput = true;
                    yield return new StreamItem
                    {
                        Response = MakeChunk(new GeminiPart
                        {
                            Text = chunk.Text,
                            Thought = IsThoughtChunk(chunk)
                        })
                    };
                    break;

                case StreamingContentType.FunctionCall:
                    if (chunk.FunctionName != null)
                    {
                        if (currentToolName != null)
                        {
                            hasOutput = true;
                            var parsedPrev = TryParseArgs(currentToolArgs);
                            yield return new StreamItem
                            {
                                Response = MakeChunk(BuildFunctionCallPart(
                                    currentToolName, currentToolId, parsedPrev), finishReason: null)
                            };
                            currentToolArgs.Clear();
                        }
                        currentToolName = chunk.FunctionName;
                        currentToolId = chunk.FunctionCallId;
                        hadToolCalls = true;
                    }
                    if (chunk.FunctionArguments != null)
                        currentToolArgs.Append(chunk.FunctionArguments);
                    break;

                case StreamingContentType.Usage when chunk.Usage != null:
                    promptTokens = ResolveUsageLong(chunk.Usage, promptTokens, ProviderUsageKeys.InputCandidates);
                    completionTokens = ResolveUsageLong(chunk.Usage, completionTokens, ProviderUsageKeys.OutputCandidates);
                    cachedTokens = ResolveUsageLong(chunk.Usage, cachedTokens, ProviderUsageKeys.CacheReadInputCandidates);
                    break;

                case StreamingContentType.RawSse when chunk.Text != null:
                    yield return new StreamItem { Raw = chunk.Text };
                    break;
            }
        }

        if (currentToolName != null)
        {
            hasOutput = true;
            var parsedArgs = TryParseArgs(currentToolArgs);
            yield return new StreamItem
            {
                Response = MakeChunk(BuildFunctionCallPart(currentToolName, currentToolId, parsedArgs))
            };
        }

        var finishReason = StopReasonMapper.DeriveGeminiFinishReason(upstreamFinishReason, hadToolCalls);
        yield return new StreamItem
        {
            Response = BuildFinalResponse([], promptTokens, completionTokens, cachedTokens, hasOutput, finishReason)
        };
    }

    private static GeminiPart BuildFunctionCallPart(string name, string? id, JsonElement? args)
    {
        var part = new GeminiPart
        {
            FunctionCall = new FunctionCall { Name = name, Args = args }
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
        bool hasOutput,
        string finishReason)
    {
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
            UsageMetadata = BuildUsageMetadata(promptTokens, completionTokens, cachedTokens)
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

    private static UsageMetadata BuildUsageMetadata(int prompt, int completion, int cached) => new()
    {
        PromptTokenCount = prompt,
        CandidatesTokenCount = completion,
        TotalTokenCount = prompt + completion,
        CachedContentTokenCount = cached > 0 ? cached : null
    };

    private static void FlushToolCall(
        List<GeminiPart> parts, ref string? toolName, ref string? toolId, System.Text.StringBuilder args)
    {
        if (toolName == null) return;

        JsonElement? parsedArgs = TryParseArgs(args);
        parts.Add(BuildFunctionCallPart(toolName, toolId, parsedArgs));

        toolName = null;
        toolId = null;
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
