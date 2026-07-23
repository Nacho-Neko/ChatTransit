using Gateway.Shared.ChatTransit.Responses;
using Gateway.Shared.Providers;
using Serilog;

namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Thrown when a request payload cannot be converged onto the backend's native
/// wire format: unknown/unregistered source format, missing registry wiring, or
/// a decode/encode failure. There is deliberately NO passthrough fallback — a
/// payload the transcoder can't understand would only reach the upstream as a
/// structurally broken request (dropped tools, wrong fields), so the task must
/// fail fast with the reason instead.
/// </summary>
public sealed class ChatTransitTranscodeException : Exception
{
    /// <summary>HTTP-ish status: 400 = caller sent an unconvertible format; 500 = worker misconfiguration.</summary>
    public int StatusCode { get; }

    /// <summary>The payload's declared source format (may be empty).</summary>
    public string SourceFormat { get; }

    /// <summary>The backend's native target format.</summary>
    public string TargetFormat { get; }

    public ChatTransitTranscodeException(
        string message, int statusCode, string sourceFormat, string targetFormat,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        SourceFormat = sourceFormat;
        TargetFormat = targetFormat;
    }
}

/// <summary>
/// Shared request-side cross-protocol transcoding helper for provider workers.
///
/// <para>Extracted from the per-worker inline logic so that
/// <c>ProviderServiceWorker</c> and the bespoke channel workers (ClaudeWorker,
/// OpenaiWorker, CursorWorker, KiroWorker, …) resolve transcoding identically:
/// the payload's wire format → the backend channel's <b>native</b> wire format.</para>
///
/// <para><b>Strict, no fallback.</b> Same-format payloads pass through untouched
/// (<see cref="RequestTranscodeResult.None"/>); everything else either transcodes
/// successfully or throws <see cref="ChatTransitTranscodeException"/> with the
/// concrete reason (unknown format / registry missing / decode-encode failure).
/// A payload that fails conversion would not be callable upstream anyway, so
/// silently forwarding the original bytes only produces corrupted requests.</para>
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
    /// Outcome of a request-side transcode. <see cref="CallerProtocol"/>
    /// and <see cref="TransitContext"/> let the response side flip direction and
    /// re-encode the <c>StreamingChunkDto</c> stream back into the caller's wire
    /// format. A same-format pass-through returns <see cref="None"/>.
    /// </summary>
    public readonly record struct RequestTranscodeResult(
        ChatTransitProtocol? CallerProtocol,
        bool ResponseTranscodeRequired,
        TransitRequest? TransitContext)
    {
        /// <summary>No transcoding performed — payload already in the backend's native format.</summary>
        public static RequestTranscodeResult None => new(null, false, null);
    }

    /// <summary>Human-readable list of the wire formats ChatTransit can decode/encode.</summary>
    private const string KnownFormats =
        ChatTransitProtocolNames.OpenAiChat + " / " + ChatTransitProtocolNames.OpenAiResponses + " / "
        + ChatTransitProtocolNames.Anthropic + " / " + ChatTransitProtocolNames.Gemini;

    /// <summary>
    /// Decodes <paramref name="request"/>'s payload from <paramref name="payloadFormat"/>
    /// and re-encodes it into <paramref name="nativeFormat"/> in place, returning the
    /// source protocol and whether the response stream needs re-encoding.
    ///
    /// <para>Returns <see cref="RequestTranscodeResult.None"/> only when no conversion
    /// is needed (formats already match, or the payload is empty and upstream parsing
    /// will surface its own error). Every other failure throws
    /// <see cref="ChatTransitTranscodeException"/> — see class remarks.</para>
    /// </summary>
    /// <exception cref="ChatTransitTranscodeException">
    /// Unknown <paramref name="payloadFormat"/>, missing registry, or decode/encode failure.
    /// </exception>
    public static RequestTranscodeResult TranscodeRequest(
        ChatTransitRegistry? transitRegistry,
        ResponseEncoderRegistry? responseEncoders,
        string? payloadFormat,
        string? nativeFormat,
        ProviderRequest request,
        string logTag,
        string taskId,
        CancellationToken ct = default)
    {
        var source = payloadFormat ?? "";
        var target = nativeFormat ?? "";

        // Same format ⇒ nothing to convert. Not a fallback: this is the declared
        // native form and the worker's own parser owns it from here.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return RequestTranscodeResult.None;

        // No payload to convert — the worker's request parser is about to reject
        // the task with its own (more precise) parse error.
        if (request.Payload is not { Length: > 0 })
            return RequestTranscodeResult.None;

        if (string.IsNullOrEmpty(target))
            throw new ChatTransitTranscodeException(
                "Worker misconfigured: no native format declared, cannot verify payload format "
                + $"'{source}'", 500, source, target);

        if (string.IsNullOrEmpty(source))
            throw new ChatTransitTranscodeException(
                $"Payload format is empty but the backend expects '{target}': the dispatcher "
                + "must set caller_format/payload_format so the request can be verified or "
                + $"transcoded (known formats: {KnownFormats})", 400, source, target);

        if (transitRegistry == null)
            throw new ChatTransitTranscodeException(
                $"Worker misconfigured: ChatTransit registry unavailable, cannot transcode "
                + $"payload format '{source}' to '{target}'", 500, source, target);

        var pair = transitRegistry.Resolve(source, target);
        if (pair is not { Decoder: not null, Encoder: not null })
        {
            // Pinpoint which side is missing so the error says exactly what was unrecognised.
            var detail = !transitRegistry.HasDecoder(source)
                ? $"unknown payload format '{source}' (no decoder registered; known formats: {KnownFormats})"
                : $"no encoder registered for native format '{target}'";
            var status = !transitRegistry.HasDecoder(source) ? 400 : 500;
            throw new ChatTransitTranscodeException(
                $"Cannot transcode request from '{source}' to '{target}': {detail}",
                status, source, target);
        }

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
                logTag, taskId, source, request.NativeRequestFormat, responseTranscodeRequired);

            return new RequestTranscodeResult(callerProtocol, responseTranscodeRequired, transitContext);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ChatTransitTranscodeException(
                $"Failed to transcode request from '{source}' to '{target}': {ex.Message}",
                400, source, target, ex);
        }
    }
}
