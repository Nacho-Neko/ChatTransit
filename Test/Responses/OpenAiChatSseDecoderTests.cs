using ChatTransit.Responses;

namespace ChatTransit.Tests.Responses;

public class OpenAiChatSseDecoderTests
{
    [Fact]
    public async Task DecodeAsync_ErrorFrame_SurfacesMessageAndCode()
    {
        var chunks = await DecodeAsync(
            """data: {"error":{"message":"Rate limit reached","type":"rate_limit_error","code":"rate_limit"}}""");

        chunks.Should().HaveCount(1);
        chunks[0].Error.Should().Be("Rate limit reached");
        chunks[0].ErrorCode.Should().Be("rate_limit_error");
    }

    [Fact]
    public async Task DecodeAsync_ErrorFrameWithStringError_SurfacesTheString()
    {
        var chunks = await DecodeAsync("""data: {"error":"upstream exploded"}""");

        chunks.Should().HaveCount(1);
        chunks[0].Error.Should().Be("upstream exploded");
    }

    /// <summary>
    /// An upstream that fails mid-stream keeps sending nothing afterwards, so the error frame
    /// has to win over — not be shadowed by — whatever partial content preceded it.
    /// </summary>
    [Fact]
    public async Task DecodeAsync_ContentThenError_YieldsBoth()
    {
        var chunks = await DecodeAsync(
            """data: {"choices":[{"index":0,"delta":{"content":"Hel"}}]}""",
            """data: {"error":{"message":"connection reset"}}""");

        chunks.Should().HaveCount(2);
        chunks[0].ContentType.Should().Be(StreamingContentType.Text);
        chunks[0].Text.Should().Be("Hel");
        chunks[1].Error.Should().Be("connection reset");
    }

    [Fact]
    public async Task DecodeAsync_NormalFrames_AreUnaffectedByTheErrorGuard()
    {
        var chunks = await DecodeAsync(
            """data: {"choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":null}]}""",
            """data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            """data: {"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":2}}""",
            "data: [DONE]");

        chunks.Should().HaveCount(3);
        chunks.Should().OnlyContain(c => c.Error == null);
        chunks[0].Text.Should().Be("hi");
        chunks[1].FinishReason.Should().Be("stop");
        chunks[2].Usage!["inputTokens"].Should().Be(11);
        chunks[2].Usage!["outputTokens"].Should().Be(2);
    }

    private static async Task<List<StreamingChunkDto>> DecodeAsync(params string[] frames)
    {
        var result = new List<StreamingChunkDto>();
        await foreach (var chunk in OpenAiChatSseDecoder.DecodeAsync(
                           ToAsync(frames), TestContext.Current.CancellationToken))
            result.Add(chunk);
        return result;
    }

    private static async IAsyncEnumerable<string> ToAsync(IEnumerable<string> frames)
    {
        foreach (var frame in frames)
        {
            yield return frame + "\n\n";
            await Task.Yield();
        }
    }
}
