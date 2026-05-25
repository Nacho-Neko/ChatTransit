using Gateway.Shared.ChatTransit.Abstractions;

namespace Gateway.Shared.ChatTransit.Errors;

/// <summary>
/// Resolves <see cref="IErrorEncoder"/> instances by <see cref="ChatTransitProtocol"/>
/// or by wire-format string. Mirrors <see cref="Responses.ResponseEncoderRegistry"/>
/// for symmetric DI access from controllers and middleware.
/// </summary>
public sealed class ErrorEncoderRegistry
{
    private readonly IReadOnlyDictionary<ChatTransitProtocol, IErrorEncoder> _encoders;

    public ErrorEncoderRegistry(IEnumerable<IErrorEncoder> encoders)
    {
        _encoders = encoders.ToDictionary(e => e.Protocol);
    }

    /// <summary>Get the error encoder for a specific protocol, or null if not registered.</summary>
    public IErrorEncoder? Get(ChatTransitProtocol protocol)
        => _encoders.TryGetValue(protocol, out var e) ? e : null;

    /// <summary>Get the error encoder by wire-format string (e.g. "openai.chat", "anthropic").</summary>
    public IErrorEncoder? Get(string? wireFormat)
    {
        var proto = wireFormat != null ? ChatTransitProtocolNames.TryParse(wireFormat) : null;
        return proto.HasValue ? Get(proto.Value) : null;
    }
}
