using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Mapping;
using Gateway.Shared.ChatTransit.Outbound;
using Gateway.Shared.ChatTransit.Responses;
using Gateway.Shared.Messaging.Serialization;
using Gateway.Shared.Providers.Streaming;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// End-to-end signature passthrough for OpenAI-format callers routed onto a
/// Gemini-native backend (Antigravity → PA → Claude/Vertex).
///
/// The opaque reasoning signature (Gemini thoughtSignature / Anthropic signature
/// tunnelled through PA) must:
///   1. be emitted to the OpenAI client on the response, then
///   2. survive replay: inbound decode → Gemini outbound re-emits thoughtSignature.
/// Otherwise PA 400s with "messages.N.content.0.thinking.signature: Field required".
/// </summary>
public class OpenAiReasoningSignatureRoundtripTests
{
    private static StreamingChunkDto Thinking(string? text, string? signature) => new()
    {
        ContentType = StreamingContentType.Thinking,
        Text = text,
        ReasoningSignature = signature,
    };

    private static string? GeminiThoughtSignatureOf(byte[] geminiRequest)
    {
        using var doc = JsonDocument.Parse(geminiRequest);
        foreach (var content in doc.RootElement.GetProperty("contents").EnumerateArray())
        {
            if (!content.TryGetProperty("parts", out var parts)) continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var t) && t.GetBoolean()
                    && part.TryGetProperty("thoughtSignature", out var sig))
                    return sig.GetString();
            }
        }
        return null;
    }

    [Fact]
    public void Responses_EncryptedContent_RoundTripsToGeminiThoughtSignature()
    {
        const string sig = "ErcBCkgIBhABGAIiQ...opaque-responses";

        // 1. Response → OpenAI Responses item carrying encrypted_content.
        var responseObj = OpenAiResponsesSseEncoder.CollectFromChunks(
            new List<StreamingChunkDto>
            {
                Thinking("considering options", null),
                Thinking(null, sig),
            },
            "gpt-5.3");
        var responseJson = JsonSerializer.Serialize(responseObj);
        using (var doc = JsonDocument.Parse(responseJson))
        {
            var reasoningItem = doc.RootElement.GetProperty("output").EnumerateArray()
                .First(o => o.GetProperty("type").GetString() == "reasoning");
            reasoningItem.GetProperty("encrypted_content").GetString().Should().Be(sig);
        }

        // 2. Client replays the reasoning item as a /v1/responses input.
        var replay = $$"""
        {
          "model": "gpt-5.3",
          "input": [
            { "type": "reasoning", "id": "rs_1",
              "summary": [{ "type": "summary_text", "text": "considering options" }],
              "encrypted_content": "{{sig}}" },
            { "type": "message", "role": "user", "content": [{ "type": "input_text", "text": "go on" }] }
          ]
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(replay));
        var thinking = transit.Messages.SelectMany(m => m.Contents)
            .First(ThinkingMapper.IsThinkingContent);
        ThinkingMapper.GetOpenAiEncryptedContent(thinking).Should().Be(sig);

        // 3. Gemini outbound re-emits it as thoughtSignature.
        var geminiReq = new GeminiOutboundEncoder().Encode(transit);
        GeminiThoughtSignatureOf(geminiReq).Should().Be(sig,
            "the OpenAI encrypted_content must round-trip to the Gemini thoughtSignature");
    }

    [Fact]
    public void ChatCompletions_ReasoningEncryptedContent_RoundTripsToGeminiThoughtSignature()
    {
        const string sig = "ErcBCkgIBhABGAIiQ...opaque-chat";

        // 1. Non-streaming Chat response carries reasoning.encrypted_content.
        var responseObj = OpenAiChatSseEncoder.CollectFromChunks(
            new List<StreamingChunkDto>
            {
                Thinking("let me reason", null),
                Thinking(null, sig),
                new() { ContentType = StreamingContentType.Text, Text = "answer" },
            },
            "gpt-5.3");
        var responseJson = JsonSerializer.Serialize(responseObj);
        using (var doc = JsonDocument.Parse(responseJson))
        {
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            msg.GetProperty("reasoning").GetProperty("encrypted_content").GetString().Should().Be(sig);
        }

        // 2. Client replays both the reasoning text AND the signature object.
        var replay = $$"""
        {
          "model": "gpt-5.3",
          "messages": [
            { "role": "assistant", "content": "answer",
              "reasoning_content": "let me reason",
              "reasoning": { "encrypted_content": "{{sig}}" } },
            { "role": "user", "content": "why?" }
          ]
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(replay));
        var thinking = transit.Messages.SelectMany(m => m.Contents)
            .First(ThinkingMapper.IsThinkingContent);
        ThinkingMapper.GetOpenAiEncryptedContent(thinking).Should().Be(sig,
            "signature must be captured even when reasoning_content text is also present");

        // 3. Gemini outbound re-emits it as thoughtSignature.
        var geminiReq = new GeminiOutboundEncoder().Encode(transit);
        GeminiThoughtSignatureOf(geminiReq).Should().Be(sig);
    }

    [Fact]
    public void CrossProtocol_OpenAiEncryptedContent_ToAnthropicBackend_EmitsSignature()
    {
        // OpenAI-format client routed onto an Anthropic-native backend: the opaque
        // signature replayed as encrypted_content must be recovered and emitted as
        // the Anthropic thinking.signature, or Claude 400s ("Field required").
        const string sig = "ErcBCkgIBhABGAIiQ...claude-backend";
        var replay = $$"""
        {
          "model": "claude-opus-4-7",
          "input": [
            { "type": "reasoning", "id": "rs_1",
              "summary": [{ "type": "summary_text", "text": "thinking" }],
              "encrypted_content": "{{sig}}" },
            { "type": "message", "role": "user", "content": [{ "type": "input_text", "text": "next" }] }
          ]
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(replay));

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var thinkingBlock = doc.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray().First(b => b.GetProperty("type").GetString() == "thinking");
        thinkingBlock.GetProperty("signature").GetString().Should().Be(sig);
    }

    [Fact]
    public void CrossProtocol_AnthropicSignature_ToOpenAiResponsesBackend_EmitsEncryptedContent()
    {
        // Anthropic-format client routed onto an OpenAI Responses backend: the
        // signature must ride back out as the reasoning item's encrypted_content.
        const string sig = "ErcBCkgIBhABGAIiQ...openai-backend";
        var replay = $$"""
        {
          "model": "gpt-5.3",
          "max_tokens": 4096,
          "messages": [
            { "role": "assistant", "content": [
              { "type": "thinking", "thinking": "thinking", "signature": "{{sig}}" },
              { "type": "text", "text": "answer" }
            ] },
            { "role": "user", "content": "why?" }
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(replay));

        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var reasoningItem = doc.RootElement.GetProperty("input").EnumerateArray()
            .First(i => i.TryGetProperty("type", out var t) && t.GetString() == "reasoning");
        reasoningItem.GetProperty("encrypted_content").GetString().Should().Be(sig);
    }

    [Fact]
    public async Task ChatCompletions_Streaming_EmitsReasoningSignatureChunk()
    {
        const string sig = "StreamChatSig==";

        async IAsyncEnumerable<StreamingChunkDto> Source()
        {
            yield return Thinking("reasoning", null);
            yield return Thinking(null, sig);
            await Task.CompletedTask;
        }

        var emitted = new List<string>();
        await foreach (var line in OpenAiChatSseEncoder.StreamAsync(Source(), "gpt-5.3"))
            emitted.Add(line);

        var signed = emitted.FirstOrDefault(l => l.Contains("encrypted_content"));
        signed.Should().NotBeNull("the stream must carry the reasoning signature");
        signed!.Should().Contain(sig);
    }
}
