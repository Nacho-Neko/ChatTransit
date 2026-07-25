using MessagePack;

namespace ChatTransit;

/// <summary>
/// Canonical streaming response chunk — the intermediate representation every
/// response decoder produces and every response encoder/collector consumes.
///
/// <para>ChatTransit is a pure protocol translator with no project reference to any
/// platform assembly, so this type is intentionally <b>not</b> shared/inherited from a
/// platform DTO (e.g. a dispatch-transport chunk type): it is ChatTransit's own,
/// self-contained canonical shape. Callers that need to cross into a platform-specific
/// wire representation (NATS chunk frames, gRPC dispatch, etc.) own the small field-by-
/// field mapping at their integration boundary — that mapping is platform plumbing, not
/// protocol translation, so it does not belong in this library.</para>
/// </summary>
[MessagePackObject]
public partial class StreamingChunkDto
{
    [Key(0)] public string? AuthorRole { get; set; }
    [Key(1)] public StreamingContentType ContentType { get; set; }
    [Key(2)] public string? Text { get; set; }
    [Key(3)] public string? FunctionName { get; set; }
    [Key(4)] public string? FunctionCallId { get; set; }
    [Key(5)] public string? FunctionArguments { get; set; }
    [Key(6)] public bool Done { get; set; }
    [Key(7)] public string? Error { get; set; }
    [Key(8)] public Dictionary<string, long>? Usage { get; set; }
    [Key(9)] public string? ModelId { get; set; }
    [Key(10)] public string? FinishReason { get; set; }
    [Key(11)] public string? ErrorCode { get; set; }
    [Key(12)] public Dictionary<string, string>? ErrorParams { get; set; }

    /// <summary>
    /// Opaque cryptographic signature attached to a reasoning/thinking block by
    /// the upstream provider (Anthropic signature, Gemini thoughtSignature).
    /// </summary>
    [Key(13)] public string? ReasoningSignature { get; set; }

    /// <summary>
    /// Opaque encrypted payload of an Anthropic <c>redacted_thinking</c> block
    /// (its <c>data</c> field). Carried so the block can be rebuilt byte-for-byte
    /// on the response path — omitting it breaks multi-turn tool calls (HTTP 400).
    /// Only ever set on <see cref="StreamingContentType.Thinking"/> chunks.
    /// </summary>
    [Key(14)] public string? RedactedThinkingData { get; set; }
}

public enum StreamingContentType : byte
{
    Text = 0,
    FunctionCall = 1,
    Usage = 2,
    Thinking = 3,
    RawSse = 4,
}
