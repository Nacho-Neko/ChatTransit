using Microsoft.Extensions.AI;

namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Intermediate representation of an inbound AI request.
/// Decoded from any supported wire protocol by an <see cref="Abstractions.IRequestDecoder"/>,
/// then re-encoded into the target protocol by an <see cref="Abstractions.IRequestEncoder"/>.
/// </summary>
public sealed class TransitRequest
{
    /// <summary>Conversation turns in canonical MEAI format.</summary>
    public required IList<ChatMessage> Messages { get; init; }

    /// <summary>Generation options (temperature, max_tokens, tools, stop sequences, etc.).</summary>
    public required ChatOptions Options { get; init; }

    /// <summary>
    /// Provider-specific or protocol-specific pass-through metadata that has no direct
    /// MEAI equivalent (cache_control, thinking.signature, encrypted_content, etc.).
    /// Keys are defined in Hints/*.cs constants; unrecognised keys are ignored by encoders.
    /// </summary>
    public required Dictionary<string, object?> Hints { get; init; }

    /// <summary>Resolved model identifier (after alias stripping, routing decisions).</summary>
    public required string Model { get; init; }

    /// <summary>Whether the client requested a streaming response.</summary>
    public required bool Stream { get; init; }

    /// <summary>
    /// Tool / function definitions from the request.
    /// Stored separately from MEAI <see cref="ChatOptions.Tools"/> to avoid
    /// the callable-AIFunction overhead.
    /// </summary>
    public IList<TransitFunctionToolDef>? FunctionTools { get; init; }
}
