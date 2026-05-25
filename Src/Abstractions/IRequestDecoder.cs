namespace Gateway.Shared.ChatTransit.Abstractions;

/// <summary>
/// Decodes raw request bytes in a specific wire protocol into a <see cref="TransitRequest"/>.
/// Implementations live in <c>Inbound/</c> and are registered per protocol.
/// </summary>
public interface IRequestDecoder
{
    /// <summary>The wire protocol this decoder handles.</summary>
    ChatTransitProtocol Protocol { get; }

    /// <summary>
    /// Decodes <paramref name="requestBytes"/> (UTF-8 JSON body) into a <see cref="TransitRequest"/>.
    /// </summary>
    /// <param name="requestBytes">Raw HTTP request body bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    TransitRequest Decode(byte[] requestBytes, CancellationToken ct = default);
}
