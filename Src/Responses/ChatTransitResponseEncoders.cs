using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.Messaging.Serialization;

namespace Gateway.Shared.ChatTransit.Responses;

/// <summary>
/// DI-injectable wrapper for <see cref="OpenAiChatSseEncoder"/> that implements
/// <see cref="IResponseSseEncoder"/> and <see cref="IResponseCollector"/>.
/// The final usage chunk is always emitted (the gateway relies on it for
/// metering), so <c>stream_options.include_usage</c> is no longer honoured here.
/// </summary>
public sealed class OpenAiChatResponseEncoder : IResponseSseEncoder, IResponseCollector
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiChat;

    public IAsyncEnumerable<string> EncodeAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks, string model,
        TransitRequest? requestContext = null, CancellationToken ct = default)
        => OpenAiChatSseEncoder.StreamAsync(chunks, model, ct);

    public object Collect(IList<StreamingChunkDto> chunks, string model)
        => OpenAiChatSseEncoder.CollectFromChunks(
            chunks as List<StreamingChunkDto> ?? chunks.ToList(), model);
}

/// <summary>
/// DI-injectable wrapper for <see cref="OpenAiResponsesSseEncoder"/>.
/// </summary>
public sealed class OpenAiResponsesResponseEncoder : IResponseSseEncoder, IResponseCollector
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiResponses;

    public IAsyncEnumerable<string> EncodeAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks, string model,
        TransitRequest? requestContext = null, CancellationToken ct = default)
        => OpenAiResponsesSseEncoder.StreamAsync(chunks, model, ct);

    public object Collect(IList<StreamingChunkDto> chunks, string model)
        => OpenAiResponsesSseEncoder.CollectFromChunks(
            chunks as List<StreamingChunkDto> ?? chunks.ToList(), model);
}

/// <summary>
/// DI-injectable wrapper for <see cref="AnthropicSseEncoder"/>.
/// </summary>
public sealed class AnthropicResponseEncoder : IResponseSseEncoder, IResponseCollector
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Anthropic;

    public IAsyncEnumerable<string> EncodeAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks, string model,
        TransitRequest? requestContext = null, CancellationToken ct = default)
        => AnthropicSseEncoder.StreamAsync(chunks, model, ct);

    public object Collect(IList<StreamingChunkDto> chunks, string model)
        => AnthropicSseEncoder.CollectFromChunks(
            chunks as List<StreamingChunkDto> ?? chunks.ToList(), model);
}

/// <summary>
/// DI-injectable wrapper for <see cref="GeminiSseEncoder"/> (SSE mode only).
/// </summary>
public sealed class GeminiResponseEncoder : IResponseSseEncoder, IResponseCollector
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Gemini;

    public IAsyncEnumerable<string> EncodeAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks, string model,
        TransitRequest? requestContext = null, CancellationToken ct = default)
        => GeminiSseEncoder.StreamSseAsync(chunks, ct);

    public object Collect(IList<StreamingChunkDto> chunks, string model)
        => GeminiSseEncoder.CollectFromChunks(chunks is List<StreamingChunkDto> list
            ? list : chunks.ToList());
}
