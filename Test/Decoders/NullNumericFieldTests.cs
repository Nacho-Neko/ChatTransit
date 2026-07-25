using ChatTransit.Inbound;
using System.Text;

namespace ChatTransit.Tests.Decoders;

/// <summary>
/// Regression: numeric option fields sent as explicit JSON null (which clients
/// routinely do) must not throw. JsonElement.TryGetInt32/TryGetDouble throw
/// InvalidOperationException on a non-Number kind, so every read is guarded with
/// a ValueKind.Number check first.
/// </summary>
public class NullNumericFieldTests
{
    [Fact]
    public void AnthropicDecoder_NullNumericFields_DoNotThrow()
    {
        var json = """
        {"model":"claude-3-5-sonnet","max_tokens":null,"temperature":null,
         "top_p":null,"top_k":null,"messages":[{"role":"user","content":"hi"}]}
        """;
        var act = () => new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var transit = act.Should().NotThrow().Which;
        transit.Options.Temperature.Should().BeNull();
        transit.Options.MaxOutputTokens.Should().BeNull();
        transit.Options.TopP.Should().BeNull();
        transit.Options.TopK.Should().BeNull();
    }

    [Fact]
    public void GeminiDecoder_NullNumericFields_DoNotThrow()
    {
        var json = """
        {"contents":[{"role":"user","parts":[{"text":"hi"}]}],
         "generationConfig":{"temperature":null,"topP":null,"topK":null,
           "maxOutputTokens":null,"seed":null,"presencePenalty":null,
           "frequencyPenalty":null,"candidateCount":null,"logprobs":null,
           "thinkingConfig":{"thinkingBudget":null}}}
        """;
        var act = () => new GeminiInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var transit = act.Should().NotThrow().Which;
        transit.Options.Temperature.Should().BeNull();
        transit.Options.MaxOutputTokens.Should().BeNull();
    }

    [Fact]
    public void OpenAiResponsesDecoder_NullNumericFields_DoNotThrow()
    {
        var json = """
        {"model":"gpt-5","input":[{"role":"user","content":"hi"}],
         "temperature":null,"top_p":null,"max_output_tokens":null,
         "frequency_penalty":null,"presence_penalty":null}
        """;
        var act = () => new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var transit = act.Should().NotThrow().Which;
        transit.Options.Temperature.Should().BeNull();
        transit.Options.MaxOutputTokens.Should().BeNull();
    }
}
