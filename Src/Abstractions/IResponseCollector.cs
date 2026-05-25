using Gateway.Shared.Messaging.Serialization;

namespace Gateway.Shared.ChatTransit.Abstractions;

/// <summary>
/// Aggregates a list of <see cref="StreamingChunkDto"/> chunks into a single
/// non-streaming response object in the client-native format.
/// </summary>
public interface IResponseCollector
{
    /// <summary>The client-facing protocol this collector produces.</summary>
    ChatTransitProtocol Protocol { get; }

    /// <summary>
    /// Collects <paramref name="chunks"/> into a non-streaming JSON-serialisable response.
    /// The returned object is passed directly to the HTTP response body serialiser.
    /// </summary>
    object Collect(IList<StreamingChunkDto> chunks, string model);
}
