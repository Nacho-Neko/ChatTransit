using ChatTransit;
using ChatTransit.Hints;
using ChatTransit.Inbound;
using ChatTransit.Mapping;
using ChatTransit.Outbound;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// Critical round-trip tests for Anthropic thinking/redacted_thinking blocks —
/// the cryptographic <c>signature</c> and <c>data</c> fields MUST survive
/// decode→IR→encode unchanged or the API rejects the next turn with HTTP 400.
/// </summary>
public class AnthropicSignatureRoundtripTests
{
    [Fact]
    public void Thinking_Block_Signature_SurvivesRoundTrip()
    {
        const string sig = "ErcBCkgIBxgCIgwBAGRdmIB8/...opaque...";
        var json = $$"""
        {
          "model": "claude-opus-4-7",
          "max_tokens": 8192,
          "messages": [
            {
              "role": "assistant",
              "content": [
                {"type": "thinking", "thinking": "Hmm, let me consider this.", "signature": "{{sig}}"},
                {"type": "text", "text": "The answer is 42."}
              ]
            },
            {"role": "user", "content": "Why?"}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));

        // IR captured the signature
        var assistantMsg = transit.Messages.First(m => m.Role == ChatRole.Assistant);
        var thinking = assistantMsg.Contents.First(ThinkingMapper.IsThinkingContent);
        ThinkingMapper.GetAnthropicSignature(thinking).Should().Be(sig);

        // Round-trip back through the encoder restores it byte-for-byte
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var assistantBlocks = doc.RootElement.GetProperty("messages")[0]
            .GetProperty("content");
        var thinkingBlock = assistantBlocks.EnumerateArray()
            .First(b => b.TryGetProperty("type", out var t) && t.GetString() == "thinking");
        thinkingBlock.GetProperty("signature").GetString().Should().Be(sig);
    }

    /// <summary>
    /// The mirror image of the round-trip above. A thinking block that arrives
    /// without a signature has no valid outbound form: Anthropic verifies the blob
    /// cryptographically and rejects a missing one with
    /// <c>"messages.N.content.0.thinking.signature: Field required"</c>, and no
    /// fabricated sentinel passes that check. Cross-protocol callers produce this
    /// shape routinely — an OpenAI client replaying a reasoning summary has nothing
    /// to put in the field — so the block is dropped and its neighbours carry on.
    /// </summary>
    [Fact]
    public void Unsigned_Thinking_Block_IsDropped_RatherThanSentWithoutOne()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 8192,
          "messages": [
            {
              "role": "assistant",
              "content": [
                {"type": "thinking", "thinking": "Reasoning that lost its signature."},
                {"type": "text", "text": "The answer is 42."}
              ]
            },
            {"role": "user", "content": "Why?"}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);

        using var doc = JsonDocument.Parse(encoded);
        var assistantContent = doc.RootElement.GetProperty("messages")[0].GetProperty("content");

        // Only the text block survives, and a lone plain text block collapses to
        // the string shorthand — so the absence of an array IS the assertion.
        assistantContent.ValueKind.Should().Be(JsonValueKind.String);
        assistantContent.GetString().Should().Be("The answer is 42.");
    }

    /// <summary>
    /// Dropping the whole turn instead would change the shape of the conversation,
    /// and Anthropic validates shape (roles alternate, the newest models reject a
    /// trailing assistant turn). The role slot is kept with the same placeholder
    /// any other content-less turn gets.
    /// </summary>
    [Fact]
    public void Turn_EmptiedByAnUnsignedThought_KeepsItsRoleSlot()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 8192,
          "messages": [
            {"role": "user", "content": "Go."},
            {"role": "assistant", "content": [
              {"type": "thinking", "thinking": "Unsigned, and the only block here."}
            ]},
            {"role": "user", "content": "Why?"}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);

        using var doc = JsonDocument.Parse(encoded);
        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);
        messages[1].GetProperty("role").GetString().Should().Be("assistant");
        messages[1].GetProperty("content").GetString().Should().Be(".");
    }

    [Fact]
    public void RedactedThinking_Data_SurvivesRoundTrip()
    {
        const string data = "REDACTED-OPAQUE-PAYLOAD-XYZ";
        var json = $$"""
        {
          "model": "claude-opus-4-7",
          "max_tokens": 8192,
          "messages": [
            {
              "role": "assistant",
              "content": [
                {"type": "redacted_thinking", "data": "{{data}}"},
                {"type": "text", "text": "Result."}
              ]
            },
            {"role": "user", "content": "OK?"}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));

        var assistant = transit.Messages.First(m => m.Role == ChatRole.Assistant);
        var redacted = assistant.Contents.First(ThinkingMapper.IsRedactedThinking);
        ThinkingMapper.GetAnthropicRedactedData(redacted).Should().Be(data);

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var assistantBlocks = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        var redactedBlock = assistantBlocks.EnumerateArray()
            .First(b => b.TryGetProperty("type", out var t) && t.GetString() == "redacted_thinking");
        redactedBlock.GetProperty("data").GetString().Should().Be(data);
    }

    [Fact]
    public void CacheControl_OnText_Block_SurvivesRoundTrip()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 1024,
          "system": [
            {"type": "text", "text": "Big preamble", "cache_control": {"type": "ephemeral"}}
          ],
          "messages": [
            {"role": "user", "content": [
              {"type": "text", "text": "Question", "cache_control": {"type": "ephemeral"}}
            ]}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));

        // The user-message text block carries the cache_control mark
        var user = transit.Messages.First(m => m.Role == ChatRole.User);
        var userText = user.Contents.OfType<TextContent>().Single();
        userText.AdditionalProperties.Should().NotBeNull();
        userText.AdditionalProperties!.Should().ContainKey(AnthropicHints.CacheControl);

        // The system blocks are preserved as a raw JSON array
        transit.Hints.Should().ContainKey(AnthropicHints.SystemBlocks);

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        // system field is the original array of blocks (with cache_control intact)
        root.GetProperty("system").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString()
            .Should().Be("ephemeral");

        // user message text block carries cache_control
        var userBlocks = root.GetProperty("messages")[0].GetProperty("content");
        userBlocks.ValueKind.Should().Be(JsonValueKind.Array);
        var firstUserBlock = userBlocks[0];
        firstUserBlock.GetProperty("cache_control").GetProperty("type").GetString()
            .Should().Be("ephemeral");
    }

    [Fact]
    public void ToolResult_StructuredContent_SurvivesRoundTrip()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 1024,
          "messages": [
            {"role": "user", "content": [
              {"type": "tool_result", "tool_use_id": "toolu_x", "is_error": true, "content": [
                {"type": "text", "text": "Error result"},
                {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "AAA="}}
              ]}
            ]}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));

        // Structured tool_result content preserved verbatim (as JsonElement)
        var user = transit.Messages.First();
        var frc = user.Contents.OfType<FunctionResultContent>().Single();
        frc.CallId.Should().Be("toolu_x");
        frc.Result.Should().BeOfType<JsonElement>();

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        content.GetProperty("type").GetString().Should().Be("tool_result");
        content.GetProperty("tool_use_id").GetString().Should().Be("toolu_x");
        content.GetProperty("is_error").GetBoolean().Should().BeTrue();
        // Array content survives — both the text and the image blocks
        content.GetProperty("content").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Document_Block_Base64_SurvivesRoundTrip()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 1024,
          "messages": [
            {"role": "user", "content": [
              {"type": "document",
               "title": "Spec",
               "context": "First quarter report",
               "source": {"type": "base64", "media_type": "application/pdf", "data": "JVBERi0="}
              }
            ]}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var block = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        block.GetProperty("type").GetString().Should().Be("document");
        block.GetProperty("source").GetProperty("media_type").GetString().Should().Be("application/pdf");
        block.GetProperty("title").GetString().Should().Be("Spec");
        block.GetProperty("context").GetString().Should().Be("First quarter report");
    }
}
