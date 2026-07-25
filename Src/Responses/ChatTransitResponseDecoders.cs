using ChatTransit.Abstractions;

namespace ChatTransit.Responses;

/// <summary>
/// DI-injectable wrapper for <see cref="OpenAiChatSseDecoder"/> that implements
/// <see cref="IResponseSseDecoder"/> and <see cref="IResponseJsonDecoder"/>.
/// </summary>
public sealed class OpenAiChatResponseDecoder : IResponseSseDecoder, IResponseJsonDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiChat;

    public IAsyncEnumerable<StreamingChunkDto> DecodeAsync(
        IAsyncEnumerable<string> nativeSseFrames, CancellationToken ct = default)
        => OpenAiChatSseDecoder.DecodeAsync(nativeSseFrames, ct);

    public List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
        => OpenAiChatSseDecoder.DecodeJson(nativeJsonBody);
}

/// <summary>
/// DI-injectable wrapper for <see cref="OpenAiResponsesSseDecoder"/>.
/// </summary>
public sealed class OpenAiResponsesResponseDecoder : IResponseSseDecoder, IResponseJsonDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiResponses;

    public IAsyncEnumerable<StreamingChunkDto> DecodeAsync(
        IAsyncEnumerable<string> nativeSseFrames, CancellationToken ct = default)
        => OpenAiResponsesSseDecoder.DecodeAsync(nativeSseFrames, ct);

    public List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
        => OpenAiResponsesSseDecoder.DecodeJson(nativeJsonBody);
}

/// <summary>
/// DI-injectable wrapper for <see cref="AnthropicSseDecoder"/>.
/// </summary>
public sealed class AnthropicResponseDecoder : IResponseSseDecoder, IResponseJsonDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Anthropic;

    public IAsyncEnumerable<StreamingChunkDto> DecodeAsync(
        IAsyncEnumerable<string> nativeSseFrames, CancellationToken ct = default)
        => AnthropicSseDecoder.DecodeAsync(nativeSseFrames, ct);

    public List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
        => AnthropicSseDecoder.DecodeJson(nativeJsonBody);
}

/// <summary>
/// DI-injectable wrapper for <see cref="GeminiSseDecoder"/>.
/// </summary>
public sealed class GeminiResponseDecoder : IResponseSseDecoder, IResponseJsonDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Gemini;

    public IAsyncEnumerable<StreamingChunkDto> DecodeAsync(
        IAsyncEnumerable<string> nativeSseFrames, CancellationToken ct = default)
        => GeminiSseDecoder.DecodeAsync(nativeSseFrames, ct);

    public List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody)
        => GeminiSseDecoder.DecodeJson(nativeJsonBody);
}
