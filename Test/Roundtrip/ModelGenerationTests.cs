using ChatTransit.Hints;
using ChatTransit.Inbound;
using ChatTransit.Mapping;
using ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// Model-generation aware encoding: modern Anthropic (Claude 4.7+ / Sonnet 5 /
/// Opus 5) rejects temperature/top_p/top_k and manual thinking:{enabled}, so the
/// encoder must omit sampling and switch to thinking:{adaptive}; legacy models
/// (≤ 4.6) keep the previous behaviour. Detection is by parsing the resolved
/// upstream model name (which the gateway rewrites onto the body before transcode).
/// </summary>
public class ModelGenerationTests
{
    private static JsonElement Encode(string model, string body)
    {
        var json = body.Replace("__MODEL__", model);
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        return JsonDocument.Parse(new AnthropicOutboundEncoder().Encode(transit)).RootElement;
    }

    private const string SamplingBody = """
    {"model":"__MODEL__","messages":[{"role":"user","content":"hi"}],
     "temperature":0.5,"top_p":0.9}
    """;

    [Theory]
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-sonnet-4-7-20260101")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-5")]
    public void Modern_Anthropic_Omits_SamplingParams(string model)
    {
        var root = Encode(model, SamplingBody);
        root.TryGetProperty("temperature", out _).Should().BeFalse();
        root.TryGetProperty("top_p", out _).Should().BeFalse();
        root.TryGetProperty("top_k", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("claude-opus-4-5")]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-3-7-sonnet-latest")]
    public void Legacy_Anthropic_Keeps_SamplingParams(string model)
    {
        var root = Encode(model, SamplingBody);
        root.GetProperty("temperature").GetDouble().Should().BeApproximately(0.25, 0.0001); // 0.5 IR ÷2
        root.GetProperty("top_p").GetDouble().Should().BeApproximately(0.9, 0.0001);
    }

    [Fact]
    public void Unknown_Anthropic_Name_Defaults_To_Legacy()
    {
        // A channel-remapped, unparseable name falls back to legacy (sampling kept).
        var root = Encode("my-custom-claude-alias", SamplingBody);
        root.TryGetProperty("temperature", out _).Should().BeTrue();
    }

    private const string ReasoningBody = """
    {"model":"__MODEL__","messages":[{"role":"user","content":"hi"}],
     "reasoning_effort":"high"}
    """;

    [Fact]
    public void Modern_Anthropic_Uses_Adaptive_Thinking()
    {
        var root = Encode("claude-opus-5", ReasoningBody);
        var thinking = root.GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be("adaptive");
        thinking.TryGetProperty("budget_tokens", out _).Should().BeFalse();
    }

    [Fact]
    public void Legacy_Anthropic_Uses_Enabled_Thinking()
    {
        var root = Encode("claude-opus-4-5", ReasoningBody);
        var thinking = root.GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be("enabled");
        thinking.GetProperty("budget_tokens").GetInt32().Should().Be(16384);
    }

    [Fact]
    public void Anthropic_Adaptive_Config_Downgrades_To_Enabled_On_Legacy_Target()
    {
        // A→A: a modern client sent thinking:{adaptive}; routed onto a legacy model
        // it must become enabled (adaptive would 400 on ≤ 4.5).
        var json = """{"model":"claude-3-5-sonnet-20241022","max_tokens":100,"thinking":{"type":"adaptive"},"messages":[{"role":"user","content":"hi"}]}""";
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var root = JsonDocument.Parse(new AnthropicOutboundEncoder().Encode(transit)).RootElement;
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
    }

    [Fact]
    public void Anthropic_Xhigh_Effort_Turns_Thinking_On_For_Legacy()
    {
        // Regression: xhigh/max previously fell through to null → thinking silently off.
        var root = Encode("claude-opus-4-5", ReasoningBody.Replace("high", "xhigh"));
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        root.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(24576);
    }

    [Theory]
    [InlineData("claude-3-5-sonnet-20241022", false)]
    [InlineData("claude-3-7-sonnet", false)]
    [InlineData("claude-sonnet-4-20250514", false)]
    [InlineData("claude-opus-4-1-20250805", false)]
    [InlineData("claude-sonnet-4-5", false)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("claude-sonnet-4-7", true)]
    [InlineData("claude-opus-4-7", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("anthropic.claude-3-5-sonnet-20241022-v2:0", false)]
    [InlineData("gpt-5", false)]
    [InlineData("", false)]
    public void ModelCapabilities_IsModernAnthropic(string model, bool expected)
        => ModelCapabilities.IsModernAnthropic(model).Should().Be(expected);

    [Theory]
    [InlineData("gemini-2.5-flash", false)]
    [InlineData("gemini-2.0-flash", false)]
    [InlineData("gemini-3-pro", true)]
    [InlineData("gemini-3-pro-preview", true)]
    [InlineData("gpt-5", false)]
    [InlineData("", false)]
    public void ModelCapabilities_GeminiSupportsThinkingLevel(string model, bool expected)
        => ModelCapabilities.GeminiSupportsThinkingLevel(model).Should().Be(expected);
}
