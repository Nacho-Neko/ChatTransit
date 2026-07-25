namespace ChatTransit.Abstractions;

/// <summary>
/// Inverse of <see cref="IResponseSseEncoder"/>: parses a provider's own native SSE
/// event stream back into canonical <see cref="StreamingChunkDto"/> values.
///
/// <para>Used by the edge (Demux.Gateway) when a request was cross-protocol
/// transcoded before dispatch: the native-only provider always answers with its
/// own native raw SSE (wrapped as <see cref="StreamingContentType.RawSse"/> chunks
/// by the provider worker — see <c>ProviderServiceWorker</c>/<c>ClaudeWorker</c>/etc.),
/// so the edge has to decode it back to neutral chunks before re-encoding into the
/// true caller's protocol via the matching <see cref="IResponseSseEncoder"/>. When
/// native format == caller format, this step is skipped entirely and the RawSse
/// chunks flow straight through the same-protocol encoder unchanged (byte-perfect
/// passthrough, today's behaviour).</para>
/// </summary>
public interface IResponseSseDecoder
{
    /// <summary>The provider-native protocol this decoder parses.</summary>
    ChatTransitProtocol Protocol { get; }

    /// <summary>
    /// Decodes a stream of raw native SSE frames (each element is one complete
    /// <c>event:</c>/<c>data:</c> block, as produced by the provider's raw SSE
    /// passthrough) into canonical chunks.
    /// </summary>
    IAsyncEnumerable<StreamingChunkDto> DecodeAsync(
        IAsyncEnumerable<string> nativeSseFrames, CancellationToken ct = default);
}
