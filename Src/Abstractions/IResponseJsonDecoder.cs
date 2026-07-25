namespace ChatTransit.Abstractions;

/// <summary>
/// Inverse of <see cref="IResponseCollector"/>: parses a provider's own native
/// non-streaming JSON response body back into a flat list of canonical
/// <see cref="StreamingChunkDto"/> values, so it can be re-collected into any
/// other protocol's non-streaming body via the matching <see cref="IResponseCollector"/>.
/// </summary>
public interface IResponseJsonDecoder
{
    /// <summary>The provider-native protocol this decoder parses.</summary>
    ChatTransitProtocol Protocol { get; }

    /// <summary>Decodes a native non-streaming JSON response body into canonical chunks.</summary>
    List<StreamingChunkDto> DecodeJson(byte[] nativeJsonBody);
}
