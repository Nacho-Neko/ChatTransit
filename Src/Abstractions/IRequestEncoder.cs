namespace Gateway.Shared.ChatTransit.Abstractions;

/// <summary>
/// Encodes a <see cref="TransitRequest"/> into raw bytes for a specific backend wire protocol.
/// Implementations live in <c>Outbound/</c> and are registered per protocol.
/// </summary>
public interface IRequestEncoder
{
    /// <summary>The wire protocol this encoder produces.</summary>
    ChatTransitProtocol Protocol { get; }

    /// <summary>
    /// Encodes <paramref name="request"/> into UTF-8 JSON bytes for the target backend.
    /// </summary>
    byte[] Encode(TransitRequest request);
}
