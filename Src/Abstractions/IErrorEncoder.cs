namespace Gateway.Shared.ChatTransit.Abstractions;

/// <summary>
/// Maps a <see cref="TransitError"/> to the client-native HTTP error body and optional
/// SSE error event string.
/// </summary>
public interface IErrorEncoder
{
    /// <summary>The client-facing protocol this encoder produces.</summary>
    ChatTransitProtocol Protocol { get; }

    /// <summary>
    /// Returns a JSON-serialisable object representing the native error body.
    /// The caller writes this to the HTTP response with <c>WriteAsJsonAsync</c>.
    /// </summary>
    object CreateBody(TransitError error);

    /// <summary>
    /// Returns a complete SSE error event string (including trailing "\n\n") suitable
    /// for writing to a streaming response, or <c>null</c> if the protocol does not
    /// use SSE-based error delivery.
    /// </summary>
    string? CreateSseEvent(TransitError error);
}
