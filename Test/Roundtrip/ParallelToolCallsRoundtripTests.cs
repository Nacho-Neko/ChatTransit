using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// P1-2: <c>parallel_tool_calls</c> (OpenAI) and
/// <c>tool_choice.disable_parallel_tool_use</c> (Anthropic) are semantically
/// equivalent toggles for serial tool dispatch. The decoder/encoder pipeline
/// must round-trip them in both directions.
/// </summary>
public class ParallelToolCallsRoundtripTests
{
    [Fact]
    public void OpenAiChat_ParallelFalse_To_Anthropic_DisableParallelTrue()
    {
        var json = """
        {
          "model": "gpt-4o",
          "messages": [{"role": "user", "content": "hi"}],
          "tools": [{"type": "function", "function": {"name": "noop"}}],
          "parallel_tool_calls": false
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        transit.Hints[OpenAiHints.ParallelToolCalls].Should().Be(false);

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var tc = doc.RootElement.GetProperty("tool_choice");
        tc.GetProperty("disable_parallel_tool_use").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void OpenAiChat_ParallelTrue_To_Anthropic_DoesNot_DisableParallel()
    {
        var json = """
        {
          "model": "gpt-4o",
          "messages": [{"role": "user", "content": "hi"}],
          "tools": [{"type": "function", "function": {"name": "noop"}}],
          "parallel_tool_calls": true,
          "tool_choice": "required"
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var tc = doc.RootElement.GetProperty("tool_choice");
        tc.TryGetProperty("disable_parallel_tool_use", out _).Should().BeFalse();
    }

    [Fact]
    public void Anthropic_DisableParallel_To_OpenAiChat_ParallelFalse()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 1024,
          "messages": [{"role": "user", "content": "hi"}],
          "tools": [{"name": "noop", "input_schema": {"type": "object"}}],
          "tool_choice": {"type": "auto", "disable_parallel_tool_use": true}
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        transit.Hints[OpenAiHints.ParallelToolCalls].Should().Be(false);

        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Anthropic_DisableParallelOmitted_DoesNot_Set_OpenAi_Hint()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 1024,
          "messages": [{"role": "user", "content": "hi"}],
          "tools": [{"name": "noop", "input_schema": {"type": "object"}}],
          "tool_choice": {"type": "auto"}
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        transit.Hints.ContainsKey(OpenAiHints.ParallelToolCalls).Should().BeFalse();
    }
}
