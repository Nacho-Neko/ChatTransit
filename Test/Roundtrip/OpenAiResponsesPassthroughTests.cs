using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Mapping;
using Gateway.Shared.ChatTransit.Outbound;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// Round-trip tests covering OpenAI Responses-specific item types that must
/// pass through verbatim (file_search_call, web_search_call, computer_call_*,
/// reasoning with encrypted_content, etc.).
/// </summary>
public class OpenAiResponsesPassthroughTests
{
    [Fact]
    public void Reasoning_Item_WithEncryptedContent_SurvivesRoundTrip()
    {
        var json = """
        {
          "model": "o3-mini",
          "input": [
            {"type": "reasoning",
             "id": "rs_abc",
             "encrypted_content": "ENCRYPTED-OPAQUE-BLOB",
             "summary": [{"type": "summary_text", "text": "step 1"}, {"type": "summary_text", "text": "step 2"}]
            },
            {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "go"}]}
          ]
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));

        // The reasoning item is captured in the IR
        var reasoningContent = transit.Messages
            .SelectMany(m => m.Contents)
            .First(ThinkingMapper.IsThinkingContent);
        ThinkingMapper.GetOpenAiEncryptedContent(reasoningContent).Should().Be("ENCRYPTED-OPAQUE-BLOB");
        ThinkingMapper.GetOpenAiReasoningItemId(reasoningContent).Should().Be("rs_abc");

        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var items = doc.RootElement.GetProperty("input");
        var reasoning = items.EnumerateArray()
            .First(i => i.TryGetProperty("type", out var t) && t.GetString() == "reasoning");
        reasoning.GetProperty("encrypted_content").GetString().Should().Be("ENCRYPTED-OPAQUE-BLOB");
        reasoning.GetProperty("id").GetString().Should().Be("rs_abc");
        reasoning.GetProperty("summary").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void FileSearchCall_PassthroughItem_SurvivesRoundTrip()
    {
        var json = """
        {
          "model": "gpt-4o",
          "input": [
            {"type": "file_search_call",
             "id": "fs_1",
             "status": "completed",
             "queries": ["foo"],
             "results": [{"file_id": "file_abc", "score": 0.9}]
            },
            {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "summarise"}]}
          ]
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        transit.Hints.Should().ContainKey(OpenAiHints.ResponsesPassthroughItems);

        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var items = doc.RootElement.GetProperty("input");
        // The passthrough item is preserved at the front of the input list
        var firstItem = items[0];
        firstItem.GetProperty("type").GetString().Should().Be("file_search_call");
        firstItem.GetProperty("id").GetString().Should().Be("fs_1");
        firstItem.GetProperty("status").GetString().Should().Be("completed");
    }

    [Fact]
    public void WebSearchCall_Passthrough_SurvivesRoundTrip()
    {
        var json = """
        {
          "model": "gpt-4o",
          "input": [
            {"type": "web_search_call", "id": "ws_1", "status": "in_progress"},
            {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "ask"}]}
          ]
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var item = doc.RootElement.GetProperty("input")[0];
        item.GetProperty("type").GetString().Should().Be("web_search_call");
        item.GetProperty("id").GetString().Should().Be("ws_1");
    }

    [Fact]
    public void Instructions_AndReasoning_HintsSurvive()
    {
        var json = """
        {
          "model": "o3-mini",
          "instructions": "You are a helpful assistant.",
          "input": [{"type": "message", "role": "user", "content": [{"type": "input_text", "text": "hi"}]}],
          "reasoning": {"effort": "high", "summary": "detailed"},
          "store": true,
          "previous_response_id": "resp_prev"
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("instructions").GetString().Should().Be("You are a helpful assistant.");
        doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("high");
        doc.RootElement.GetProperty("store").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("previous_response_id").GetString().Should().Be("resp_prev");
    }

    [Fact]
    public void BuiltinTools_Tools_SurviveRoundTrip()
    {
        var json = """
        {
          "model": "gpt-4o",
          "input": [{"type": "message", "role": "user", "content": [{"type": "input_text", "text": "go"}]}],
          "tools": [
            {"type": "file_search", "vector_store_ids": ["vs_1"]},
            {"type": "function", "name": "my_fn"}
          ]
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();
        tools.Should().HaveCount(2);
        tools.Should().Contain(t => t.GetProperty("type").GetString() == "file_search");
        tools.Should().Contain(t => t.GetProperty("type").GetString() == "function");
    }
}
