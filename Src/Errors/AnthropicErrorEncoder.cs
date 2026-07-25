using ChatTransit.Abstractions;
using ChatTransit.Anthropic;
using System.Text.Json;

namespace ChatTransit.Errors;

/// <summary>
/// Builds Anthropic-format error response objects and SSE error events.
/// Implements <see cref="IErrorEncoder"/> for DI-driven cross-protocol error projection.
/// </summary>
public sealed class AnthropicErrorEncoder : IErrorEncoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Anthropic;

    public object CreateBody(TransitError error)
        => new AnthropicErrorResponse
        {
            Error = new AnthropicErrorDetail
            {
                Type = MapErrorType(error),
                Message = error.Message
            }
        };

    public string? CreateSseEvent(TransitError error)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "error",
            error = new
            {
                type = MapErrorType(error),
                message = error.Message
            }
        });
        return $"event: error\ndata: {payload}\n\n";
    }

    // ── Legacy static helpers (kept for callers that haven't migrated to DI) ──

    public static AnthropicErrorResponse CreateResponse(string type, string message)
        => new() { Error = new AnthropicErrorDetail { Type = type, Message = message } };

    public static string FormatStreamError(string type, string message)
    {
        var errorPayload = JsonSerializer.Serialize(new
        {
            type = "error",
            error = new { type, message }
        });
        return $"event: error\ndata: {errorPayload}\n\n";
    }

    public static int MapStatusCode(string errorCode) => errorCode switch
    {
        "invalid_request_error" => 400,
        "authentication_error" => 401,
        "permission_error" or "permission_denied" => 403,
        "not_found_error" => 404,
        "request_too_large" => 413,
        "rate_limit_error" => 429,
        "api_error" => 500,
        "overloaded_error" => 529,
        _ => 500
    };

    // ── Mapping ───────────────────────────────────────────────────────────────

    // Anthropic's official error `type` vocabulary. A provider-original type is only
    // adopted when it belongs to this set; otherwise (e.g. OpenAI's
    // insufficient_quota / context_length_exceeded) we fall back to the HTTP-status
    // mapping so the Anthropic error envelope stays spec-conformant.
    private static readonly HashSet<string> AnthropicErrorTypes = new(StringComparer.Ordinal)
    {
        "invalid_request_error",
        "authentication_error",
        "permission_error",
        "not_found_error",
        "request_too_large",
        "rate_limit_error",
        "api_error",
        "overloaded_error",
    };

    private static string MapErrorType(TransitError error)
    {
        // Prefer the provider-original type only when it is a recognised Anthropic type.
        if (!string.IsNullOrEmpty(error.ProviderErrorType)
            && AnthropicErrorTypes.Contains(error.ProviderErrorType!))
            return error.ProviderErrorType!;

        return error.StatusCode switch
        {
            400 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            413 => "request_too_large",
            429 => "rate_limit_error",
            503 => "overloaded_error",
            529 => "overloaded_error",
            _ => "api_error"
        };
    }
}
