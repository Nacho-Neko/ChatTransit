using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

public class CrossProtocolToolResultAndFormatTests
{
    /// <summary>
    /// P1-5: Anthropic <c>tool_result.content</c> as an array containing a text
    /// block + an image block must produce an OpenAI Chat tool message with
    /// content as an array of <c>{type:"text"}</c> + <c>{type:"image_url"}</c>
    /// parts — the image must survive translation.
    /// </summary>
    [Fact]
    public void Anthropic_ToolResultWithImage_To_OpenAiChat_PreservesImage()
    {
        var json = """
        {
          "model": "claude-opus-4-7",
          "max_tokens": 1024,
          "messages": [
            {"role": "user", "content": [
              {"type": "tool_result", "tool_use_id": "toolu_x", "content": [
                {"type": "text", "text": "Here is the chart"},
                {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "iVBORw0K"}}
              ]}
            ]}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiChatOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);

        var toolMsg = doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "tool");
        toolMsg.GetProperty("tool_call_id").GetString().Should().Be("toolu_x");
        var content = toolMsg.GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.Array);
        var types = content.EnumerateArray().Select(b => b.GetProperty("type").GetString()).ToList();
        types.Should().Contain("text");
        types.Should().Contain("image_url");
        var imageBlock = content.EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "image_url");
        imageBlock.GetProperty("image_url").GetProperty("url").GetString()
            .Should().StartWith("data:image/png;base64,");
    }

    /// <summary>
    /// P1-6: <c>response_format:{type:"json_schema", json_schema:{schema:{...}}}</c>
    /// on the inbound OpenAI Chat request must translate to Gemini's
    /// <c>generationConfig.responseSchema</c> + <c>responseMimeType:"application/json"</c>
    /// when no explicit Gemini hint is present.
    /// </summary>
    [Fact]
    public void OpenAiChat_ResponseFormatJsonSchema_To_Gemini_ResponseSchema()
    {
        var json = """
        {
          "model": "gpt-4o",
          "messages": [{"role": "user", "content": "name an animal"}],
          "response_format": {
            "type": "json_schema",
            "json_schema": {
              "name": "animal",
              "schema": {
                "type": "object",
                "properties": {"name": {"type": "string"}},
                "required": ["name"]
              }
            }
          }
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new GeminiOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var gc = doc.RootElement.GetProperty("generationConfig");
        gc.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        var schema = gc.GetProperty("responseSchema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString()
            .Should().Be("string");
    }

    [Fact]
    public void OpenAiChat_ResponseFormatJsonObject_To_Gemini_OnlySetsMime()
    {
        var json = """
        {
          "model": "gpt-4o",
          "messages": [{"role": "user", "content": "hi"}],
          "response_format": {"type": "json_object"}
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new GeminiOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var gc = doc.RootElement.GetProperty("generationConfig");
        gc.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        gc.TryGetProperty("responseSchema", out _).Should().BeFalse();
    }

    /// <summary>
    /// P1-7: built-in Gemini tools (<c>googleSearch</c>, <c>urlContext</c>,
    /// <c>codeExecution</c>) must round-trip through the decoder/encoder pair
    /// even when they aren't <c>functionDeclarations</c>.
    /// </summary>
    [Fact]
    public void Gemini_BuiltinTools_RoundTrip()
    {
        var json = """
        {
          "contents": [{"role": "user", "parts": [{"text": "search"}]}],
          "tools": [
            {"googleSearch": {}},
            {"urlContext": {}},
            {"functionDeclarations": [{"name": "extra"}]}
          ]
        }
        """;
        var transit = new GeminiInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        transit.Hints.Should().ContainKey(GeminiHints.BuiltinTools);
        transit.FunctionTools.Should().HaveCount(1);
        transit.FunctionTools![0].Name.Should().Be("extra");

        var encoded = new GeminiOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var tools = doc.RootElement.GetProperty("tools");
        tools.GetArrayLength().Should().Be(3);
        var keys = tools.EnumerateArray()
            .SelectMany(t => t.EnumerateObject().Select(p => p.Name))
            .ToHashSet();
        keys.Should().Contain("googleSearch");
        keys.Should().Contain("urlContext");
        keys.Should().Contain("functionDeclarations");
    }
}
