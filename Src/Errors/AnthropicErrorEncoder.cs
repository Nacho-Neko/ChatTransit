using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Anthropic;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Errors;

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

    private static string MapErrorType(TransitError error)
    {
        // Prefer the provider-original type when present and recognised.
        if (!string.IsNullOrEmpty(error.ProviderErrorType))
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
