namespace ChatTransit.Abstractions;

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
    /// <see cref="IReadOnlyList{T}"/> (not <c>List</c>) so callers holding a
    /// <c>List</c> of a <see cref="StreamingChunkDto"/> subtype — e.g. a caller
    /// that only sees its own transport-level DTO subclass — can pass it straight
    /// through without a copy.
    /// </summary>
    object Collect(IReadOnlyList<StreamingChunkDto> chunks, string model);
}
