using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Gemini;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Errors;

/// <summary>
/// Builds Gemini-format error response objects and SSE error events.
/// </summary>
public sealed class GeminiErrorEncoder : IErrorEncoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Gemini;

    public object CreateBody(TransitError error)
        => new GeminiErrorResponse
        {
            Error = new GeminiErrorDetail
            {
                Code = error.StatusCode,
                Message = error.Message,
                Status = error.ProviderErrorType ?? MapStatus(error.StatusCode)
            }
        };

    public string? CreateSseEvent(TransitError error)
    {
        var payload = JsonSerializer.Serialize(CreateBody(error));
        return $"data: {payload}\n\n";
    }

    // ── Legacy static helpers ─────────────────────────────────────────────────

    public static GeminiErrorResponse CreateResponse(int code, string message, string? status = null)
        => new() { Error = new GeminiErrorDetail { Code = code, Message = message, Status = status ?? MapStatus(code) } };

    public static string FormatStreamError(int code, string message, string? status = null)
    {
        var errorJson = JsonSerializer.Serialize(new GeminiErrorResponse
        {
            Error = new GeminiErrorDetail { Code = code, Message = message, Status = status ?? MapStatus(code) }
        });
        return $"data: {errorJson}\n\n";
    }

    public static string MapStatus(int statusCode) => statusCode switch
    {
        400 => "INVALID_ARGUMENT",
        401 => "UNAUTHENTICATED",
        403 => "PERMISSION_DENIED",
        404 => "NOT_FOUND",
        409 => "ALREADY_EXISTS",
        429 => "RESOURCE_EXHAUSTED",
        499 => "CANCELLED",
        500 => "INTERNAL",
        501 => "UNIMPLEMENTED",
        503 => "UNAVAILABLE",
        504 => "DEADLINE_EXCEEDED",
        _ => "INTERNAL"
    };
}
