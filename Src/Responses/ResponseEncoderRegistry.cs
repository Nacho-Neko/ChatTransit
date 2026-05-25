using Gateway.Shared.ChatTransit.Abstractions;

namespace Gateway.Shared.ChatTransit.Responses;

/// <summary>
/// Resolves <see cref="IResponseSseEncoder"/> and <see cref="IResponseCollector"/>
/// instances by <see cref="ChatTransitProtocol"/>.
/// </summary>
public sealed class ResponseEncoderRegistry
{
    private readonly IReadOnlyDictionary<ChatTransitProtocol, IResponseSseEncoder> _sseEncoders;
    private readonly IReadOnlyDictionary<ChatTransitProtocol, IResponseCollector> _collectors;

    public ResponseEncoderRegistry(
        IEnumerable<IResponseSseEncoder> sseEncoders,
        IEnumerable<IResponseCollector> collectors)
    {
        _sseEncoders = sseEncoders.ToDictionary(e => e.Protocol);
        _collectors = collectors.ToDictionary(c => c.Protocol);
    }

    public IResponseSseEncoder? GetSseEncoder(ChatTransitProtocol protocol)
        => _sseEncoders.TryGetValue(protocol, out var e) ? e : null;

    public IResponseCollector? GetCollector(ChatTransitProtocol protocol)
        => _collectors.TryGetValue(protocol, out var c) ? c : null;

    public IResponseSseEncoder? GetSseEncoder(string? wireFormat)
    {
        var proto = wireFormat != null ? ChatTransitProtocolNames.TryParse(wireFormat) : null;
        return proto.HasValue ? GetSseEncoder(proto.Value) : null;
    }

    public IResponseCollector? GetCollector(string? wireFormat)
    {
        var proto = wireFormat != null ? ChatTransitProtocolNames.TryParse(wireFormat) : null;
        return proto.HasValue ? GetCollector(proto.Value) : null;
    }
}
