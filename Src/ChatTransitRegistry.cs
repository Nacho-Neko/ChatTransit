using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Outbound;

namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Resolves the (IRequestDecoder, IRequestEncoder) pair needed to convert a request
/// from <c>callerFormat</c> to <c>nativeFormat</c>.
///
/// When caller and native format are the same, returns <c>null</c> to indicate
/// zero-copy passthrough — no transcoding is needed.
/// </summary>
public sealed class ChatTransitRegistry
{
    private readonly IReadOnlyDictionary<string, IRequestDecoder> _decoders;
    private readonly IReadOnlyDictionary<string, IRequestEncoder> _encoders;

    public ChatTransitRegistry(
        IEnumerable<IRequestDecoder> decoders,
        IEnumerable<IRequestEncoder> encoders)
    {
        _decoders = decoders.ToDictionary(
            d => ChatTransitProtocolNames.ToWireString(d.Protocol),
            StringComparer.OrdinalIgnoreCase);

        _encoders = encoders.ToDictionary(
            e => ChatTransitProtocolNames.ToWireString(e.Protocol),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the decoder/encoder pair for the given format transition.
    /// </summary>
    /// <returns>
    /// <c>null</c> when caller format equals native format (passthrough short-circuit).
    /// A tuple with both the decoder and encoder otherwise.
    /// <c>null</c> decoder means the caller format is not a known ChatTransit format.
    /// <c>null</c> encoder means the native format is not a known ChatTransit format.
    /// </returns>
    public (IRequestDecoder? Decoder, IRequestEncoder? Encoder)? Resolve(
        string callerFormat, string nativeFormat)
    {
        if (string.Equals(callerFormat, nativeFormat, StringComparison.OrdinalIgnoreCase))
            return null; // same-protocol passthrough — no conversion needed

        _decoders.TryGetValue(callerFormat, out var decoder);
        _encoders.TryGetValue(nativeFormat, out var encoder);

        if (decoder == null || encoder == null)
            return null; // unregistered format — fall back to passthrough

        return (decoder, encoder);
    }

    /// <summary>Whether a decoder is registered for the given format string.</summary>
    public bool HasDecoder(string format) => _decoders.ContainsKey(format);

    /// <summary>Whether an encoder is registered for the given format string.</summary>
    public bool HasEncoder(string format) => _encoders.ContainsKey(format);
}
