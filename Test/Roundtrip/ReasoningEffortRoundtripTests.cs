using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// P1-1: <c>reasoning_effort</c> is captured from the top of OpenAI Chat
/// requests (officially documented for o-series / gpt-5), written back on
/// OpenAI Chat → OpenAI Chat round-trips, and translated to Anthropic
/// <c>thinking.budget_tokens</c> / Gemini <c>thinkingConfig.thinkingLevel</c>
/// on cross-protocol routes.
/// </summary>
public class ReasoningEffortRoundtripTests
{
    [Fact]
    public void OpenAiChat_ReasoningEffort_RoundTrip()
    {
        var json = """
        {
          "model": "gpt-5",
          "messages": [{"role": "user", "content": "Plan a refactor"}],
          "reasoning_effort": "high"
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        transit.Hints[OpenAiHints.ReasoningEffort].Should().Be("high");

        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
    }

    [Theory]
    [InlineData("minimal", 1024)]
    [InlineData("low", 4096)]
    [InlineData("medium", 8192)]
    [InlineData("high", 16384)]
    public void OpenAiChat_ReasoningEffort_To_Anthropic_BudgetTokens(string effort, int expectedBudget)
    {
        var json = """
        {
          "model": "gpt-5",
          "messages": [{"role": "user", "content": "hi"}],
          "reasoning_effort": "__EFFORT__"
        }
        """.Replace("__EFFORT__", effort);
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var thinking = doc.RootElement.GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be("enabled");
        thinking.GetProperty("budget_tokens").GetInt32().Should().Be(expectedBudget);
        // Thinking mode strips temperature/top_p/top_k.
        doc.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("minimal", "low")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    public void OpenAiChat_ReasoningEffort_To_Gemini_ThinkingLevel(string effort, string expectedLevel)
    {
        var json = """
        {
          "model": "gpt-5",
          "messages": [{"role": "user", "content": "hi"}],
          "reasoning_effort": "__EFFORT__"
        }
        """.Replace("__EFFORT__", effort);
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new GeminiOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var thinking = doc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        thinking.GetProperty("thinkingLevel").GetString().Should().Be(expectedLevel);
    }

    [Fact]
    public void OpenAiResponses_ReasoningObject_AlsoSets_EffortHint()
    {
        // Cross-protocol Responses → Anthropic should pick up the effort scalar
        // from `reasoning.effort` (since reasoning lives in a sub-object).
        var json = """
        {
          "model": "gpt-5",
          "input": [{"role": "user", "content": "hi"}],
          "reasoning": {"effort": "medium"}
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        transit.Hints[OpenAiHints.ReasoningEffort].Should().Be("medium");

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(8192);
    }
}
