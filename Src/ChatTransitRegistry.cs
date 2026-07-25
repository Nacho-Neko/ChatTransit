using ChatTransit.Abstractions;
using ChatTransit.Inbound;
using ChatTransit.Outbound;

namespace ChatTransit;

/// <summary>
/// Resolves the (IRequestDecoder, IRequestEncoder) pair needed to convert a request
/// from <c>callerFormat</c> to <c>nativeFormat</c>.
///
/// When caller and native format are the same, returns <c>null</c> to indicate
/// zero-copy passthrough — no transcoding is needed.
/// </summary>
public sealed class ChatTransitRegistry
{
    private readonly IReadOnlyDictionary<ChatTransitProtocol, IRequestDecoder> _decoders;
    private readonly IReadOnlyDictionary<ChatTransitProtocol, IRequestEncoder> _encoders;

    public ChatTransitRegistry(
        IEnumerable<IRequestDecoder> decoders,
        IEnumerable<IRequestEncoder> encoders)
    {
        _decoders = decoders.ToDictionary(d => d.Protocol);
        _encoders = encoders.ToDictionary(e => e.Protocol);
    }

    /// <summary>
    /// Resolves the decoder/encoder pair for the given format transition.
    /// </summary>
    /// <returns>
    /// <c>null</c> when caller format equals native format (passthrough short-circuit),
    /// or when either side has no registered decoder/encoder.
    /// A tuple with both the decoder and encoder otherwise.
    /// </returns>
    public (IRequestDecoder? Decoder, IRequestEncoder? Encoder)? Resolve(
        ChatTransitProtocol callerFormat, ChatTransitProtocol nativeFormat)
    {
        if (callerFormat == nativeFormat)
            return null; // same-protocol passthrough — no conversion needed

        _decoders.TryGetValue(callerFormat, out var decoder);
        _encoders.TryGetValue(nativeFormat, out var encoder);

        if (decoder == null || encoder == null)
            return null; // unregistered format — fall back to passthrough

        return (decoder, encoder);
    }

    /// <summary>
    /// Wire-string overload for callers holding a raw format label straight off the
    /// wire (dispatch envelope, provider catalog). An unparseable label resolves to
    /// <c>null</c>, i.e. the same passthrough as an unregistered protocol.
    /// </summary>
    public (IRequestDecoder? Decoder, IRequestEncoder? Encoder)? Resolve(
        string? callerFormat, string? nativeFormat)
        => ChatTransitProtocolNames.TryParse(callerFormat) is { } caller
            && ChatTransitProtocolNames.TryParse(nativeFormat) is { } native
                ? Resolve(caller, native)
                : null;

    /// <summary>Whether a decoder is registered for the given protocol.</summary>
    public bool HasDecoder(ChatTransitProtocol protocol) => _decoders.ContainsKey(protocol);

    /// <summary>Whether an encoder is registered for the given protocol.</summary>
    public bool HasEncoder(ChatTransitProtocol protocol) => _encoders.ContainsKey(protocol);
}
