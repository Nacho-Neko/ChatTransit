using ChatTransit.Abstractions;
using ChatTransit.OpenAi;
using System.Text.Json;

namespace ChatTransit.Errors;

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
                Param = GetParam(error),
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
                param = GetParam(error),
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
    /// Maps HTTP status codes to the vocabulary OpenAI actually emits in the
    /// <c>error.type</c> field. Reference:
    /// <see href="https://platform.openai.com/docs/guides/error-codes/api-errors"/>.
    /// OpenAI overwhelmingly returns <c>invalid_request_error</c> for 4xx client
    /// errors (differentiated by the machine-readable <c>code</c>, e.g.
    /// <c>invalid_api_key</c>, <c>model_not_found</c>), <c>rate_limit_error</c> for
    /// non-quota 429s, and <c>api_error</c> for 5xx. Quota exhaustion
    /// (<c>insufficient_quota</c>) is handled in <see cref="MapType"/>.
    /// </summary>
    public static string MapStatusType(int statusCode) => statusCode switch
    {
        400 or 401 or 403 or 404 or 409 or 422 => "invalid_request_error",
        429 => "rate_limit_error",
        >= 500 => "api_error",
        _ => "invalid_request_error"
    };

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static string MapType(TransitError error)
    {
        if (!string.IsNullOrEmpty(error.ProviderErrorType))
            return error.ProviderErrorType!;
        // A 429 caused by billing/quota carries OpenAI's dedicated type rather than
        // the retryable rate-limit type.
        if (error.StatusCode == 429
            && error.ErrorCode.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return "insufficient_quota";
        return MapStatusType(error.StatusCode);
    }

    /// <summary>
    /// Extracts the offending parameter name from <see cref="TransitError.Extra"/>
    /// when present. The key is always emitted on the wire (as <c>null</c> when
    /// unknown), matching OpenAI's error-object shape.
    /// </summary>
    private static object? GetParam(TransitError error)
        => error.Extra != null && error.Extra.TryGetValue("param", out var p) ? p : null;
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
