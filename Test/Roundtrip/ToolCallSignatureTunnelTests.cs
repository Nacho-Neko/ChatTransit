using ChatTransit.Inbound;
using ChatTransit.Mapping;
using ChatTransit.Outbound;
using ChatTransit.Responses;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// Gemini 3 signs the <c>functionCall</c> part and rejects the next turn unless the
/// signature comes back on that same part. Chat Completions has no field of its own
/// for it, so Google defines <c>tool_calls[].extra_content.google.thought_signature</c>
/// ("Signatures for OpenAI compatibility",
/// https://ai.google.dev/gemini-api/docs/thought-signatures) and both returns and
/// validates it there. These tests walk the loop a caller actually performs —
/// response out, assistant message echoed back in, request re-encoded for Gemini —
/// and assert the blob lands back on the native part.
///
/// <para>Chat Completions only, deliberately: Google documents no carrier for the
/// Responses API or for Anthropic Messages, and those fall back to the sentinel
/// prescribed for signature-less histories instead of a shape no client would
/// produce.</para>
/// </summary>
public class ToolCallSignatureTunnelTests
{
    private const string Sig = "CvQBAdHtim9abcOPAQUE==";

    private static List<StreamingChunkDto> SignedToolCallChunks() =>
    [
        new()
        {
            ContentType = StreamingContentType.FunctionCall,
            FunctionName = "lookup",
            FunctionCallId = "call_1",
            FunctionArguments = """{"q":"weather"}""",
            ReasoningSignature = Sig,
        },
    ];

    private static JsonElement Reparse(object value)
    {
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    private static string? WireSignature(JsonElement toolCall)
        => toolCall
            .GetProperty(ThinkingMapper.OpenAiExtraContentKey)
            .GetProperty(ThinkingMapper.OpenAiExtraContentVendorKey)
            .GetProperty(ThinkingMapper.OpenAiThoughtSignatureKey)
            .GetString();

    [Fact]
    public void NonStreaming_SignatureReachesClient_AndReturnsToTheGeminiFunctionCallPart()
    {
        var response = Reparse(OpenAiChatSseEncoder.CollectFromChunks(
            SignedToolCallChunks(), "gemini-3-pro"));

        var message = response.GetProperty("choices")[0].GetProperty("message");
        WireSignature(message.GetProperty("tool_calls")[0])
            .Should().Be(Sig, "the caller can only replay a signature it was given");

        // The client appends the assistant message it received and asks for more.
        var request = $$"""
        {"model":"gemini-3-pro","messages":[{{message.GetRawText()}}]}
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(request));

        var fcc = transit.Messages.SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>().Single();
        ThinkingMapper.GetGeminiThoughtSignature(fcc).Should().Be(Sig);

        var encoded = new GeminiOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0]
            .GetProperty("thoughtSignature").GetString().Should().Be(Sig);
    }

    [Fact]
    public void Streaming_SignatureRidesOnTheOpeningToolCallDelta()
    {
        // Google's own compat endpoint puts it on the delta that carries id + name.
        var sse = string.Concat(OpenAiChatSseEncoder.StreamAsync(
            SignedToolCallChunks().ToAsyncEnumerable(), "gemini-3-pro")
            .ToBlockingEnumerable());

        var openingDelta = sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f["data: ".Length..])
            .Where(d => d != "[DONE]")
            .Select(d => JsonDocument.Parse(d).RootElement.Clone())
            .First(d => d.GetProperty("choices")[0].GetProperty("delta")
                .TryGetProperty("tool_calls", out var tc)
                && tc[0].TryGetProperty("id", out _));

        WireSignature(openingDelta.GetProperty("choices")[0]
            .GetProperty("delta").GetProperty("tool_calls")[0]).Should().Be(Sig);
    }

    [Fact]
    public void UnsignedToolCall_LeavesExtraContentOffTheWire()
    {
        // Every non-Gemini backend produces unsigned tool calls; none of them should
        // start carrying a Google-specific object.
        List<StreamingChunkDto> chunks =
        [
            new()
            {
                ContentType = StreamingContentType.FunctionCall,
                FunctionName = "lookup",
                FunctionCallId = "call_1",
                FunctionArguments = "{}",
            },
        ];

        var response = Reparse(OpenAiChatSseEncoder.CollectFromChunks(chunks, "gpt-4o"));
        response.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("tool_calls")[0]
            .TryGetProperty(ThinkingMapper.OpenAiExtraContentKey, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void SequentialToolCalls_EachKeepTheirOwnSignature()
    {
        // Google: "when there are sequential function calls (multi-step), each
        // function call will have a signature and you must pass all signatures back".
        List<StreamingChunkDto> chunks =
        [
            new()
            {
                ContentType = StreamingContentType.FunctionCall,
                FunctionName = "first", FunctionCallId = "call_1",
                FunctionArguments = "{}", ReasoningSignature = "sig-one",
            },
            new()
            {
                ContentType = StreamingContentType.FunctionCall,
                FunctionName = "second", FunctionCallId = "call_2",
                FunctionArguments = "{}", ReasoningSignature = "sig-two",
            },
        ];

        var response = Reparse(OpenAiChatSseEncoder.CollectFromChunks(chunks, "gemini-3-pro"));
        var toolCalls = response.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("tool_calls");

        WireSignature(toolCalls[0]).Should().Be("sig-one");
        WireSignature(toolCalls[1]).Should().Be("sig-two",
            "a signature must not leak from the call it belongs to");
    }

    [Fact]
    public void ParallelToolCalls_UnsignedFollowersStayUnsigned()
    {
        // Google: with parallel calls only the first part carries a signature, and
        // every part must be replayed exactly as received — so we must not invent one
        // for the followers.
        List<StreamingChunkDto> chunks =
        [
            new()
            {
                ContentType = StreamingContentType.FunctionCall,
                FunctionName = "first", FunctionCallId = "call_1",
                FunctionArguments = "{}", ReasoningSignature = Sig,
            },
            new()
            {
                ContentType = StreamingContentType.FunctionCall,
                FunctionName = "second", FunctionCallId = "call_2",
                FunctionArguments = "{}",
            },
        ];

        var response = Reparse(OpenAiChatSseEncoder.CollectFromChunks(chunks, "gemini-3-pro"));
        var toolCalls = response.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("tool_calls");

        WireSignature(toolCalls[0]).Should().Be(Sig);
        toolCalls[1].TryGetProperty(ThinkingMapper.OpenAiExtraContentKey, out _)
            .Should().BeFalse();
    }
}
