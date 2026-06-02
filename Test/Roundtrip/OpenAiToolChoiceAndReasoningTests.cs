using Gateway.Shared.ChatTransit;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Mapping;
using Gateway.Shared.ChatTransit.Outbound;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

public class OpenAiToolChoiceAndReasoningTests
{
    // ── tool_choice projection ────────────────────────────────────────────────

    [Theory]
    [InlineData("auto")]
    [InlineData("required")]
    [InlineData("none")]
    public void OpenAiChat_StringToolChoice_RoundTrip(string toolChoice)
    {
        var json =
            "{\"model\":\"gpt-4o\"," +
            "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," +
            "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"noop\"}}]," +
            $"\"tool_choice\":\"{toolChoice}\"}}";
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var tc = doc.RootElement.GetProperty("tool_choice");
        tc.ValueKind.Should().Be(JsonValueKind.String);
        tc.GetString().Should().Be(toolChoice);
    }

    [Fact]
    public void OpenAiChat_FunctionToolChoice_RoundTrip()
    {
        var json = """
        {
          "model": "gpt-4o",
          "messages": [{"role": "user", "content": "hi"}],
          "tools": [{"type": "function", "function": {"name": "my_func"}}],
          "tool_choice": {"type": "function", "function": {"name": "my_func"}}
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var tc = doc.RootElement.GetProperty("tool_choice");
        tc.GetProperty("type").GetString().Should().Be("function");
        tc.GetProperty("function").GetProperty("name").GetString().Should().Be("my_func");
    }

    // ── reasoning_content (DeepSeek / Qwen extension on chat.completions) ─────

    [Fact]
    public void OpenAiChat_ReasoningContent_RoundTrip()
    {
        var json = """
        {
          "model": "deepseek-reasoner",
          "messages": [
            {"role": "user", "content": "1+1?"},
            {"role": "assistant", "reasoning_content": "Internal CoT", "content": "2"}
          ]
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var assistant = transit.Messages.First(m => m.Role == ChatRole.Assistant);
        assistant.Contents.Should().Contain(c => ThinkingMapper.IsThinkingContent(c));

        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var assistantOut = doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "assistant");
        assistantOut.GetProperty("reasoning_content").GetString().Should().Be("Internal CoT");
        assistantOut.GetProperty("content").GetString().Should().Be("2");
    }

    // ── stream_options.include_usage is always forced on for streaming ────────

    [Fact]
    public void OpenAiChat_Streaming_AlwaysRequestsUsage()
    {
        // The client did NOT set stream_options.include_usage, but the outbound
        // encoder must still force it on so the gateway can meter tokens.
        var json = """
        {
          "model": "gpt-4o",
          "messages": [{"role": "user", "content": "hi"}],
          "stream": true
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));

        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean()
            .Should().BeTrue();
    }
}
