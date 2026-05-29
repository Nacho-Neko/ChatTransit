using Gateway.Shared.ChatTransit.Responses;
using Gateway.Shared.Providers;
using Serilog;

namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Shared request-side cross-protocol transcoding helper for provider workers.
///
/// <para>Extracted from the per-worker inline logic so that
/// <c>ProviderServiceWorker</c> and the bespoke channel workers (GeminiWorker,
/// …) resolve transcoding identically: caller wire format → the backend
/// channel's <b>native</b> wire format.</para>
///
/// <para>The conversion is resolved against the backend's own native format
/// (e.g. "gemini") rather than <see cref="ProviderRequest.NativeRequestFormat"/>,
/// which <see cref="ProviderRequest.FromNativePayload"/> seeds to the caller
/// format — resolving against that would compare a format to itself and never
/// transcode.</para>
/// </summary>
public static class ChatTransitTaskTranscoder
{
    /// <summary>
    /// Outcome of a request-side transcode attempt. <see cref="CallerProtocol"/>
    /// and <see cref="TransitContext"/> let the response side flip direction and
    /// re-encode the <c>StreamingChunkDto</c> stream back into the caller's wire
    /// format. A same-format pass-through returns <see cref="None"/>.
    /// </summary>
    public readonly record struct RequestTranscodeResult(
        ChatTransitProtocol? CallerProtocol,
        bool ResponseTranscodeRequired,
        TransitRequest? TransitContext)
    {
        /// <summary>No transcoding performed — historical structured-chunk passthrough.</summary>
        public static RequestTranscodeResult None => new(null, false, null);
    }

    /// <summary>
    /// Decodes <paramref name="request"/>'s payload from <paramref name="callerFormat"/>
    /// and re-encodes it into <paramref name="nativeFormat"/> in place, returning the
    /// caller protocol and whether the response stream needs re-encoding.
    ///
    /// <para>Returns <see cref="RequestTranscodeResult.None"/> (leaving the payload
    /// untouched) when the registry is absent, either format is missing/unregistered,
    /// the formats match, or decoding throws — every failure degrades to passthrough.</para>
    /// </summary>
    public static RequestTranscodeResult TryTranscodeRequest(
        ChatTransitRegistry? transitRegistry,
        ResponseEncoderRegistry? responseEncoders,
        string? callerFormat,
        string? nativeFormat,
        ProviderRequest request,
        string logTag,
        string taskId,
        CancellationToken ct = default)
    {
        if (transitRegistry == null
            || string.IsNullOrEmpty(callerFormat)
            || string.IsNullOrEmpty(nativeFormat)
            || request.Payload is not { Length: > 0 })
        {
            return RequestTranscodeResult.None;
        }

        var pair = transitRegistry.Resolve(callerFormat, nativeFormat);
        if (pair is not { Decoder: not null, Encoder: not null })
            return RequestTranscodeResult.None;

        try
        {
            var transitContext = pair.Value.Decoder!.Decode(request.Payload, ct);
            request.Payload = pair.Value.Encoder!.Encode(transitContext);
            var callerProtocol = pair.Value.Decoder.Protocol;
            request.NativeRequestFormat = ChatTransitProtocolNames.ToWireString(pair.Value.Encoder.Protocol);

            var responseTranscodeRequired = responseEncoders != null
                && responseEncoders.GetSseEncoder(callerProtocol) != null;

            Log.Debug(
                "[{Tag}] ChatTransit: transcoded task {TaskId} from {From} to {To} (responseTranscode={Resp})",
                logTag, taskId, callerFormat, request.NativeRequestFormat, responseTranscodeRequired);

            return new RequestTranscodeResult(callerProtocol, responseTranscodeRequired, transitContext);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "[{Tag}] ChatTransit transcoding failed for task {TaskId} ({From}→{To}), using original payload",
                logTag, taskId, callerFormat, nativeFormat);
            return RequestTranscodeResult.None;
        }
    }
}
