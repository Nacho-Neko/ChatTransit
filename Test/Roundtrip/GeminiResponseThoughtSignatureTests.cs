using Gateway.Shared.ChatTransit.Responses;
using Gateway.Shared.Messaging.Serialization;
using Gateway.Shared.Providers.Streaming;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// Response-side tests for the Gemini encoder's thoughtSignature passthrough.
/// The opaque signature must be re-emitted on the thought part so a Gemini-format
/// caller can replay it next turn (Gemini 3 thinking continuity; Claude-via-PA
/// "messages.N.content.0.thinking.signature: Field required").
/// </summary>
public class GeminiResponseThoughtSignatureTests
{
    private static StreamingChunkDto Thinking(string? text, string? signature) => new()
    {
        ContentType = StreamingContentType.Thinking,
        Text = text,
        ReasoningSignature = signature,
    };

    [Fact]
    public void NonStreaming_Aggregation_EmitsThoughtSignature()
    {
        const string sig = "ErcBCkgIBhABGAIiQ...opaque";
        var chunks = new List<StreamingChunkDto>
        {
            Thinking("let me think", null),
            Thinking(null, sig),
            new() { ContentType = StreamingContentType.Text, Text = "final answer" },
        };

        var response = GeminiSseEncoder.CollectFromChunks(chunks);

        var parts = response.Candidates![0].Content!.Parts!;
        var thoughtPart = parts.First(p => p.Thought);
        thoughtPart.Text.Should().Be("let me think");
        thoughtPart.ThoughtSignature.Should().Be(sig, "the signature must survive aggregation");
    }

    [Fact]
    public void NonStreaming_SignatureOnly_StillEmitsThoughtPart()
    {
        const string sig = "SignatureOnlyNoText==";
        var chunks = new List<StreamingChunkDto> { Thinking(null, sig) };

        var response = GeminiSseEncoder.CollectFromChunks(chunks);

        var thoughtPart = response.Candidates![0].Content!.Parts!.First(p => p.Thought);
        thoughtPart.ThoughtSignature.Should().Be(sig);
    }

    [Fact]
    public async Task Streaming_EmitsThoughtSignatureChunk()
    {
        const string sig = "StreamThoughtSig==";

        async IAsyncEnumerable<StreamingChunkDto> Source()
        {
            yield return Thinking("reasoning", sig);
            await Task.CompletedTask;
        }

        var emitted = new List<string>();
        await foreach (var line in GeminiSseEncoder.StreamSseAsync(Source()))
            emitted.Add(line);

        var signedChunk = emitted.FirstOrDefault(l => l.Contains("thoughtSignature"));
        signedChunk.Should().NotBeNull("the streamed thought chunk must carry the signature");

        var payload = signedChunk!["data: ".Length..];
        using var doc = JsonDocument.Parse(payload);
        var thoughtPart = doc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0];
        thoughtPart.GetProperty("thought").GetBoolean().Should().BeTrue();
        thoughtPart.GetProperty("thoughtSignature").GetString().Should().Be(sig);
    }
}
