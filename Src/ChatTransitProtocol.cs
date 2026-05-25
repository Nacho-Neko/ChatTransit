namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Identifies the wire protocol a client request arrives in, or the format a backend expects.
/// Used by ChatTransitRegistry to pick the correct Decoder and Encoder pair.
/// </summary>
public enum ChatTransitProtocol
{
    /// <summary>OpenAI Chat Completions API (/v1/chat/completions).</summary>
    OpenAiChat,

    /// <summary>OpenAI Responses API (/v1/responses) — distinct schema from Chat Completions.</summary>
    OpenAiResponses,

    /// <summary>Anthropic Messages API (/v1/messages).</summary>
    Anthropic,

    /// <summary>Google Gemini generateContent API.</summary>
    Gemini,
}

/// <summary>
/// Wire format ID strings — these must match what controllers put in <c>CallerFormat</c>
/// (e.g. "openai.chat", "openai.responses", "anthropic", "gemini") and what
/// <see cref="Provider.Shared.Channels.ISdkProviderChannel.NativeFormat"/> returns.
/// </summary>
public static class ChatTransitProtocolNames
{
    /// <summary>OpenAI Chat Completions wire format (used by OpenAiController).</summary>
    public const string OpenAiChat = "openai.chat";

    /// <summary>OpenAI Responses API wire format.</summary>
    public const string OpenAiResponses = "openai.responses";

    /// <summary>Anthropic Messages API wire format.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>Google Gemini generateContent wire format.</summary>
    public const string Gemini = "gemini";

    /// <summary>
    /// Tries to parse a wire format string into a <see cref="ChatTransitProtocol"/>.
    /// Returns <c>null</c> if the format is unknown (allows passthrough for unregistered formats).
    /// </summary>
    public static ChatTransitProtocol? TryParse(string? name) => name?.ToLowerInvariant() switch
    {
        "openai.chat" or "openai-chat" or "openai" => ChatTransitProtocol.OpenAiChat,
        "openai.responses" or "openai-responses" or "openai_responses" => ChatTransitProtocol.OpenAiResponses,
        "anthropic" or "claude" => ChatTransitProtocol.Anthropic,
        "gemini" or "google" => ChatTransitProtocol.Gemini,
        _ => null
    };

    public static string ToWireString(ChatTransitProtocol protocol) => protocol switch
    {
        ChatTransitProtocol.OpenAiChat => OpenAiChat,
        ChatTransitProtocol.OpenAiResponses => OpenAiResponses,
        ChatTransitProtocol.Anthropic => Anthropic,
        ChatTransitProtocol.Gemini => Gemini,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };
}
