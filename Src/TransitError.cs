namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Unified error representation used internally within ChatTransit.
/// Each <see cref="Abstractions.IErrorEncoder"/> maps this to the client-native error format.
/// </summary>
/// <param name="StatusCode">HTTP status code: 400/401/403/404/429/500/503/504.</param>
/// <param name="ErrorCode">
/// Internal code, typically matching a value from <c>GatewayErrorCode</c>.
/// E.g. "model_not_found", "rate_limit_exceeded", "upstream_error".
/// </param>
/// <param name="Message">Human-readable, already-localised message.</param>
/// <param name="ProviderErrorType">
/// The original provider-side error type string, if available
/// (e.g. Anthropic "overloaded_error", OpenAI "insufficient_quota").
/// </param>
/// <param name="Extra">
/// Optional structured extra fields for richer protocol error payloads
/// (e.g. retry-after seconds, param name that caused the error).
/// </param>
public sealed record TransitError(
    int StatusCode,
    string ErrorCode,
    string Message,
    string? ProviderErrorType = null,
    IReadOnlyDictionary<string, object?>? Extra = null)
{
    public static TransitError BadRequest(string message, string code = "invalid_request_error")
        => new(400, code, message);

    public static TransitError Unauthorized(string message)
        => new(401, "authentication_error", message);

    public static TransitError Forbidden(string message)
        => new(403, "permission_denied", message);

    public static TransitError NotFound(string message)
        => new(404, "not_found_error", message);

    public static TransitError RateLimit(string message, string? providerType = null)
        => new(429, "rate_limit_error", message, providerType);

    public static TransitError ServiceUnavailable(string message, string? providerType = null)
        => new(503, "service_unavailable", message, providerType);

    public static TransitError Internal(string message, string? providerType = null)
        => new(500, "internal_error", message, providerType);
}
