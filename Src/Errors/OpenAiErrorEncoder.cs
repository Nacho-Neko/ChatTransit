using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.OpenAi;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Errors;

/// <summary>
/// Builds OpenAI-format error response objects and SSE error events.
/// Used for both Chat Completions and Responses API — the wire format is
/// identical and the SSE delivery framing differs only by the trailing
/// <c>[DONE]</c> marker (chat completions) which we always emit.
/// </summary>
public sealed class OpenAiErrorEncoder : IErrorEncoder
{
    public ChatTransitProtocol Protocol { get; }

    public OpenAiErrorEncoder() : this(ChatTransitProtocol.OpenAiChat) { }

    public OpenAiErrorEncoder(ChatTransitProtocol protocol)
    {
        Protocol = protocol;
    }

    public object CreateBody(TransitError error)
        => new OpenAiErrorResponse
        {
            Error = new OpenAiError
            {
                Message = error.Message,
                Type = MapType(error),
                Code = error.ErrorCode
            }
        };

    public string? CreateSseEvent(TransitError error)
    {
        var payload = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = error.Message,
                type = MapType(error),
                code = error.ErrorCode
            }
        });
        // Chat Completions delivers errors as a data line followed by [DONE];
        // Responses API expects the same data framing minus the [DONE] sentinel.
        return Protocol == ChatTransitProtocol.OpenAiChat
            ? $"data: {payload}\n\ndata: [DONE]\n\n"
            : $"event: error\ndata: {payload}\n\n";
    }

    // ── Legacy static helpers (kept for callers that haven't migrated to DI) ──

    public static OpenAiErrorResponse CreateResponse(string message, string type, object? code = null)
        => new() { Error = new OpenAiError { Message = message, Type = type, Code = code } };

    public static string FormatStreamError(string message, string type = "internal_error")
    {
        var escaped = message.Replace("\"", "\\\"");
        return $"data: {{\"error\":{{\"message\":\"{escaped}\",\"type\":\"{type}\"}}}}\n\ndata: [DONE]\n\n";
    }

    /// <summary>
    /// Maps HTTP status codes to the official OpenAI <c>error.type</c> enum.
    /// Reference: <see href="https://platform.openai.com/docs/guides/error-codes/api-errors"/>.
    /// Only the canonical strings the SDK and docs use are emitted — historical
    /// vendor extensions (e.g. <c>permission_denied_error</c>,
    /// <c>service_unavailable_error</c>) are normalised away.
    /// </summary>
    public static string MapStatusType(int statusCode) => statusCode switch
    {
        400 or 422 => "invalid_request_error",
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        409 => "conflict_error",
        429 => "rate_limit_error",
        _ => "api_error"
    };

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static string MapType(TransitError error)
    {
        if (!string.IsNullOrEmpty(error.ProviderErrorType))
            return error.ProviderErrorType!;
        return MapStatusType(error.StatusCode);
    }
}

/// <summary>
/// Concrete <see cref="IErrorEncoder"/> specialisation for the OpenAI Responses
/// API. Same payload shape as Chat Completions — only the SSE event framing differs.
/// </summary>
public sealed class OpenAiResponsesErrorEncoder : IErrorEncoder
{
    private readonly OpenAiErrorEncoder _inner = new(ChatTransitProtocol.OpenAiResponses);

    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiResponses;
    public object CreateBody(TransitError error) => _inner.CreateBody(error);
    public string? CreateSseEvent(TransitError error) => _inner.CreateSseEvent(error);
}
