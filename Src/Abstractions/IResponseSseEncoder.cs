using Gateway.Shared.Messaging.Serialization;

namespace Gateway.Shared.ChatTransit.Abstractions;

/// <summary>
/// Converts a stream of <see cref="StreamingChunkDto"/> values into SSE lines in
/// the client-native wire format.
/// </summary>
public interface IResponseSseEncoder
{
    /// <summary>The client-facing protocol this encoder produces.</summary>
    ChatTransitProtocol Protocol { get; }

    /// <summary>
    /// Transforms canonical <paramref name="chunks"/> into SSE strings.
    /// Each yielded string is a complete SSE event block (ends with "\n\n").
    /// <paramref name="requestContext"/> carries the original <see cref="TransitRequest"/>
    /// (when available) so encoders can honour caller-side hints such as
    /// <c>stream_options.include_usage</c>.
    /// </summary>
    IAsyncEnumerable<string> EncodeAsync(
        IAsyncEnumerable<StreamingChunkDto> chunks,
        string model,
        TransitRequest? requestContext = null,
        CancellationToken ct = default);
}
