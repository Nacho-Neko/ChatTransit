using Gateway.Shared.ChatTransit;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Outbound;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ChatTransit.Tests.CrossProtocol;

/// <summary>
/// Round-trip tests: decode from protocol A, then encode to protocol B.
/// Verifies key fields survive the IR transformation.
/// </summary>
public class CrossProtocolRoundTripTests
{
    // ── OpenAI Chat → Anthropic ──────────────────────────────────────────────

    [Fact]
    public void OpenAiChat_To_Anthropic_SimpleText_RoundTrip()
    {
        var json = """{"model":"gpt-4o","stream":false,"messages":[{"role":"system","content":"Be helpful."},{"role":"user","content":"Hi!"}]}""";
        var transit = new OpenAiChatInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("system", out var sys).Should().BeTrue();
        sys.GetString().Should().Contain("Be helpful");

        root.GetProperty("messages").GetArrayLength().Should().Be(1);
        var firstMsg = root.GetProperty("messages")[0];
        firstMsg.GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public void OpenAiChat_To_Anthropic_ToolCalls_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("openai_chat_tool_calls.json");
        var transit = new OpenAiChatInboundDecoder().Decode(bytes);
        var encoded = new AnthropicOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("tools", out var tools).Should().BeTrue();
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("get_weather");

        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);

        // Last message should contain tool_result
        var lastMsg = messages[2];
        var content = lastMsg.GetProperty("content");
        if (content.ValueKind == JsonValueKind.Array)
        {
            var hasToolResult = content.EnumerateArray()
                .Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_result");
            hasToolResult.Should().BeTrue();
        }
    }

    // ── OpenAI Chat → Gemini ─────────────────────────────────────────────────

    [Fact]
    public void OpenAiChat_To_Gemini_SimpleText_RoundTrip()
    {
        var json = """{"model":"gpt-4o","stream":false,"messages":[{"role":"system","content":"You are an expert."},{"role":"user","content":"Explain quantum computing."}]}""";
        var transit = new OpenAiChatInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        var encoded = new GeminiOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("systemInstruction", out var si).Should().BeTrue();
        si.GetProperty("parts")[0].GetProperty("text").GetString().Should().Contain("expert");

        root.GetProperty("contents").GetArrayLength().Should().Be(1);
        var firstContent = root.GetProperty("contents")[0];
        firstContent.GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public void OpenAiChat_To_Gemini_ToolCalls_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("openai_chat_tool_calls.json");
        var transit = new OpenAiChatInboundDecoder().Decode(bytes);
        var encoded = new GeminiOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("tools", out var tools).Should().BeTrue();
        var decls = tools[0].GetProperty("functionDeclarations");
        decls.GetArrayLength().Should().Be(1);
        decls[0].GetProperty("name").GetString().Should().Be("get_weather");

        var contents = root.GetProperty("contents");
        // The model's tool_call should be in the model role
        var modelContent = contents.EnumerateArray()
            .FirstOrDefault(c => c.TryGetProperty("role", out var r) && r.GetString() == "model");
        modelContent.ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    // ── Anthropic → OpenAI Chat ──────────────────────────────────────────────

    [Fact]
    public void Anthropic_To_OpenAiChat_SimpleText_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_simple.json");
        var transit = new AnthropicInboundDecoder().Decode(bytes);
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().Should().Be("claude-opus-4-7");
        root.GetProperty("stream").GetBoolean().Should().BeTrue();

        var messages = root.GetProperty("messages");
        var roles = messages.EnumerateArray()
            .Select(m => m.TryGetProperty("role", out var r) ? r.GetString() : null)
            .ToList();
        roles.Should().Contain("system");
        roles.Should().Contain("user");
    }

    [Fact]
    public void Anthropic_To_OpenAiChat_ToolCalls_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_tool_calls.json");
        var transit = new AnthropicInboundDecoder().Decode(bytes);
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("tools", out var tools).Should().BeTrue();
        tools[0].GetProperty("type").GetString().Should().Be("function");
        tools[0].GetProperty("function").GetProperty("name").GetString().Should().Be("web_search");
    }

    // ── Gemini → OpenAI Chat ─────────────────────────────────────────────────

    [Fact]
    public void Gemini_To_OpenAiChat_SimpleText_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_simple.json");
        var transit = new GeminiInboundDecoder().Decode(bytes);
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().BeGreaterThan(0);
        messages[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public void Gemini_To_Anthropic_ToolCalls_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_tool_calls.json");
        var transit = new GeminiInboundDecoder().Decode(bytes);
        var encoded = new AnthropicOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("tools", out var tools).Should().BeTrue();
        tools[0].GetProperty("name").GetString().Should().Be("get_weather");
    }

    // ── Anthropic → Gemini ───────────────────────────────────────────────────

    [Fact]
    public void Anthropic_To_Gemini_Thinking_PreservesHints()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_thinking.json");
        var transit = new AnthropicInboundDecoder().Decode(bytes);

        transit.Hints.Should().ContainKey(Gateway.Shared.ChatTransit.Hints.AnthropicHints.IsThinkingModel);

        var encoded = new GeminiOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);

        // Gemini output may not have a direct "thinking" field; just verify it builds without throwing
        doc.RootElement.TryGetProperty("contents", out _).Should().BeTrue();
    }

    // ── OpenAI Responses → Gemini ────────────────────────────────────────────

    [Fact]
    public void OpenAiResponses_To_Gemini_SimpleText_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("openai_responses_simple.json");
        var transit = new OpenAiResponsesInboundDecoder().Decode(bytes);
        var encoded = new GeminiOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("systemInstruction", out var si).Should().BeTrue();
        si.GetProperty("parts")[0].GetProperty("text").GetString()
            .Should().Contain("helpful assistant");

        var contents = root.GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        contents[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public void OpenAiResponses_To_Anthropic_ToolCalls_RoundTrip()
    {
        var bytes = FixtureLoader.LoadBytes("openai_responses_tool_calls.json");
        var transit = new OpenAiResponsesInboundDecoder().Decode(bytes);
        var encoded = new AnthropicOutboundEncoder().Encode(transit);

        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;

        root.TryGetProperty("tools", out var tools).Should().BeTrue();
        tools[0].GetProperty("name").GetString().Should().Be("get_stock_price");
    }

    // ── Sampling-parameter mapping (temperature / top_p / top_k / max_tokens) ──

    [Fact]
    public void OpenAiChat_To_Anthropic_Scalars_Survive()
    {
        // OpenAI temperature 0.7 (on its 0-2 scale) is "low-moderate randomness".
        // The semantically equivalent point on Anthropic's 0-1 scale is 0.35.
        // top_p ranges are identical so no rescale.
        var json = """{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}],"temperature":0.7,"top_p":0.9,"max_tokens":256,"stop":["END"]}""";
        var transit = new OpenAiChatInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        transit.Options.Temperature.Should().BeApproximately(0.7f, 0.0001f);
        transit.Options.TopP.Should().BeApproximately(0.9f, 0.0001f);
        transit.Options.MaxOutputTokens.Should().Be(256);
        transit.Options.StopSequences.Should().BeEquivalentTo(["END"]);

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;
        root.GetProperty("temperature").GetDouble().Should().BeApproximately(0.35, 0.0001);
        root.GetProperty("top_p").GetDouble().Should().BeApproximately(0.9, 0.0001);
        root.GetProperty("max_tokens").GetInt32().Should().Be(256);
        root.GetProperty("stop_sequences")[0].GetString().Should().Be("END");
    }

    [Fact]
    public void Anthropic_TopK_RoundTripsTo_Gemini()
    {
        // Anthropic temperature 0.3 (on 0-1 scale) decodes to IR 0.6 (on 0-2 scale),
        // then re-encodes to Gemini as 0.6 (Gemini and IR share the 0-2 scale).
        var json = """{"model":"claude-opus-4-7","max_tokens":128,"messages":[{"role":"user","content":"hi"}],"temperature":0.3,"top_p":0.95,"top_k":40}""";
        var transit = new AnthropicInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        transit.Options.TopK.Should().Be(40);
        transit.Options.Temperature.Should().BeApproximately(0.6f, 0.0001f); // rescaled into IR

        var encoded = new GeminiOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        var gc = doc.RootElement.GetProperty("generationConfig");
        gc.GetProperty("topK").GetInt32().Should().Be(40);
        gc.GetProperty("topP").GetDouble().Should().BeApproximately(0.95, 0.0001);
        gc.GetProperty("temperature").GetDouble().Should().BeApproximately(0.6, 0.0001);
    }

    [Fact]
    public void Gemini_TopK_RoundTripsTo_Anthropic()
    {
        // Gemini 0.2 stays 0.2 in IR (same scale), then Anthropic encoder ÷2 → 0.1.
        var json = """{"contents":[{"role":"user","parts":[{"text":"hi"}]}],"generationConfig":{"temperature":0.2,"topP":0.8,"topK":20,"maxOutputTokens":512}}""";
        var transit = new GeminiInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        transit.Options.TopK.Should().Be(20);
        transit.Options.MaxOutputTokens.Should().Be(512);

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;
        root.GetProperty("top_k").GetInt32().Should().Be(20);
        root.GetProperty("top_p").GetDouble().Should().BeApproximately(0.8, 0.0001);
        root.GetProperty("temperature").GetDouble().Should().BeApproximately(0.1, 0.0001);
        root.GetProperty("max_tokens").GetInt32().Should().Be(512);
    }

    // ── Sampling-scale rescale (Anthropic 0-1 ↔ OpenAI/Gemini/IR 0-2) ─────────

    [Theory]
    [InlineData(0.0, 0.0)]    // anchor: deterministic
    [InlineData(1.0, 0.5)]    // OpenAI middle (1.0) → Anthropic middle (0.5)
    [InlineData(2.0, 1.0)]    // OpenAI max (2.0) → Anthropic max (1.0)
    [InlineData(1.5, 0.75)]   // arbitrary point along the line
    public void OpenAiChat_To_Anthropic_Temperature_RescalesProportionally(
        double openAiTemp, double expectedAnthropicTemp)
    {
        var json = $$"""{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}],"temperature":{{openAiTemp}}}""";
        var transit = new OpenAiChatInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("temperature").GetDouble()
            .Should().BeApproximately(expectedAnthropicTemp, 0.0001);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 1.0)]    // Anthropic middle → OpenAI middle
    [InlineData(1.0, 2.0)]    // Anthropic max → OpenAI max
    public void Anthropic_To_OpenAiChat_Temperature_RescalesProportionally(
        double anthropicTemp, double expectedOpenAiTemp)
    {
        var json = $$"""{"model":"claude-opus-4-7","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"temperature":{{anthropicTemp}}}""";
        var transit = new AnthropicInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("temperature").GetDouble()
            .Should().BeApproximately(expectedOpenAiTemp, 0.0001);
    }

    [Fact]
    public void Anthropic_Temperature_RoundTrip_IsLossless_WithinPrecision()
    {
        // Round-trip through IR: Anthropic 0.42 → IR 0.84 → Anthropic 0.42.
        var json = """{"model":"claude-opus-4-7","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"temperature":0.42}""";
        var transit = new AnthropicInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("temperature").GetDouble()
            .Should().BeApproximately(0.42, 0.0001);
    }

    [Fact]
    public void OpenAiChat_Temperature_OutOfRange_IsClampedAtDecodeBoundary()
    {
        // Clients sending temperature > 2.0 (or < 0) violate OpenAI's spec; we
        // clamp at the decoder boundary so downstream encoders never see a
        // value that would 4xx the upstream regardless of which provider runs.
        var json = """{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}],"temperature":2.5}""";
        var transit = new OpenAiChatInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        transit.Options.Temperature.Should().Be(2.0f);

        var negativeJson = """{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}],"temperature":-0.5}""";
        var negTransit = new OpenAiChatInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(negativeJson));
        negTransit.Options.Temperature.Should().Be(0.0f);
    }

    [Fact]
    public void OpenAiChat_OversizedTemperature_ClampedTo_AnthropicMax()
    {
        // The whole point of the rescale: a caller that sends OpenAI's max
        // temperature (2.0) must NOT get rejected by Anthropic. After clamp +
        // rescale we send exactly 1.0 — Anthropic's legal maximum.
        var json = """{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}],"temperature":3.0}""";
        var transit = new OpenAiChatInboundDecoder().Decode(System.Text.Encoding.UTF8.GetBytes(json));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        doc.RootElement.GetProperty("temperature").GetDouble().Should().Be(1.0);
    }

    [Fact]
    public void Anthropic_Thinking_StripsSamplingParams()
    {
        // Thinking mode rejects temperature/top_p/top_k overrides.
        var bytes = FixtureLoader.LoadBytes("anthropic_thinking.json");
        var transit = new AnthropicInboundDecoder().Decode(bytes);

        // Force-set sampling so we can verify they get stripped.
        transit.Options.Temperature = 0.5f;
        transit.Options.TopP = 0.7f;
        transit.Options.TopK = 50;

        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        var root = doc.RootElement;
        root.TryGetProperty("temperature", out _).Should().BeFalse();
        root.TryGetProperty("top_p", out _).Should().BeFalse();
        root.TryGetProperty("top_k", out _).Should().BeFalse();
        root.TryGetProperty("thinking", out _).Should().BeTrue();
    }

    [Fact]
    public void OpenAiChat_TopK_NotEmitted_NoSuchField()
    {
        // OpenAI Chat has no top_k; even if IR carries one (from a cross-protocol
        // caller like Anthropic/Gemini), the encoder must silently drop it rather
        // than send a field the API doesn't recognise.
        var transit = new TransitRequest
        {
            Model = "gpt-4o",
            Stream = false,
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
            Options = new ChatOptions { Temperature = 0.5f, TopK = 40 },
            Hints = new Dictionary<string, object?>(),
        };
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        var doc = JsonDocument.Parse(encoded);
        doc.RootElement.TryGetProperty("top_k", out _).Should().BeFalse();
    }

    // ── ChatTransitRegistry same-protocol passthrough ────────────────────────

    [Fact]
    public void Registry_SameProtocol_ReturnsNull()
    {
        var registry = BuildRegistry();
        var result = registry.Resolve("openai.chat", "openai.chat");
        result.Should().BeNull();
    }

    [Fact]
    public void Registry_CrossProtocol_ReturnsEncoderDecoderPair()
    {
        var registry = BuildRegistry();
        var result = registry.Resolve("openai.chat", "gemini");
        result.Should().NotBeNull();
        result!.Value.Decoder.Should().BeOfType<OpenAiChatInboundDecoder>();
        result!.Value.Encoder.Should().BeOfType<GeminiOutboundEncoder>();
    }

    [Fact]
    public void Registry_UnknownFormat_ReturnsNull()
    {
        var registry = BuildRegistry();
        var result = registry.Resolve("unknown-proto", "gemini");
        result.Should().BeNull();
    }

    private static ChatTransitRegistry BuildRegistry()
    {
        var decoders = new Gateway.Shared.ChatTransit.Abstractions.IRequestDecoder[]
        {
            new OpenAiChatInboundDecoder(),
            new OpenAiResponsesInboundDecoder(),
            new AnthropicInboundDecoder(),
            new GeminiInboundDecoder(),
        };
        var encoders = new Gateway.Shared.ChatTransit.Abstractions.IRequestEncoder[]
        {
            new OpenAiChatOutboundEncoder(),
            new OpenAiResponsesOutboundEncoder(),
            new AnthropicOutboundEncoder(),
            new GeminiOutboundEncoder(),
        };
        return new ChatTransitRegistry(decoders, encoders);
    }
}
