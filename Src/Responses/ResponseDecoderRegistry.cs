using ChatTransit.Abstractions;

namespace ChatTransit.Responses;

/// <summary>
/// Resolves <see cref="IResponseSseDecoder"/> and <see cref="IResponseJsonDecoder"/>
/// instances by <see cref="ChatTransitProtocol"/>. Mirrors <see cref="ResponseEncoderRegistry"/>
/// but for the opposite direction: native provider response → neutral chunks.
/// </summary>
public sealed class ResponseDecoderRegistry
{
    private readonly IReadOnlyDictionary<ChatTransitProtocol, IResponseSseDecoder> _sseDecoders;
    private readonly IReadOnlyDictionary<ChatTransitProtocol, IResponseJsonDecoder> _jsonDecoders;

    public ResponseDecoderRegistry(
        IEnumerable<IResponseSseDecoder> sseDecoders,
        IEnumerable<IResponseJsonDecoder> jsonDecoders)
    {
        _sseDecoders = sseDecoders.ToDictionary(d => d.Protocol);
        _jsonDecoders = jsonDecoders.ToDictionary(d => d.Protocol);
    }

    public IResponseSseDecoder? GetSseDecoder(ChatTransitProtocol protocol)
        => _sseDecoders.TryGetValue(protocol, out var d) ? d : null;

    public IResponseJsonDecoder? GetJsonDecoder(ChatTransitProtocol protocol)
        => _jsonDecoders.TryGetValue(protocol, out var d) ? d : null;

    public IResponseSseDecoder? GetSseDecoder(string? wireFormat)
    {
        var proto = wireFormat != null ? ChatTransitProtocolNames.TryParse(wireFormat) : null;
        return proto.HasValue ? GetSseDecoder(proto.Value) : null;
    }

    public IResponseJsonDecoder? GetJsonDecoder(string? wireFormat)
    {
        var proto = wireFormat != null ? ChatTransitProtocolNames.TryParse(wireFormat) : null;
        return proto.HasValue ? GetJsonDecoder(proto.Value) : null;
    }
}
