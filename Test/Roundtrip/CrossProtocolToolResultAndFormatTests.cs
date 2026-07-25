using ChatTransit.Hints;
using ChatTransit.Inbound;
using ChatTransit.Outbound;
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
    /// Anthropic <c>tool_result</c> carries only <c>tool_use_id</c> — the
    /// function name exists solely on the paired <c>tool_use</c>. The Gemini
    /// encoder must recover that name and echo the id, otherwise the
    /// functionResponse goes out as <c>{name: "&lt;id&gt;"}</c> with no id and
    /// downstream Anthropic adapters (Antigravity PA) mint a mismatched
    /// tool_use_id and 400 ("unexpected tool_use_id found in tool_result blocks").
    /// </summary>
    [Fact]
    public void Anthropic_ToolResult_To_Gemini_RestoresFunctionNameAndId()
    {
        var json = """
        {
          "model": "claude-opus-4-6-thinking",
          "max_tokens": 1024,
          "messages": [
            {"role": "user", "content": "read the file"},
            {"role": "assistant", "content": [
              {"type": "tool_use", "id": "tooluse_YwZf4rS6VWyN2otOui0mEz", "name": "read_file", "input": {"path": "a.txt"}}
            ]},
            {"role": "user", "content": [
              {"type": "tool_result", "tool_use_id": "tooluse_YwZf4rS6VWyN2otOui0mEz", "content": "file contents"}
            ]}
          ]
        }
        """;
        var transit = new AnthropicInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new GeminiOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);

        var contents = doc.RootElement.GetProperty("contents").EnumerateArray().ToList();

        var fc = contents[1].GetProperty("parts").EnumerateArray()
            .First(p => p.TryGetProperty("functionCall", out _))
            .GetProperty("functionCall");
        fc.GetProperty("name").GetString().Should().Be("read_file");
        fc.GetProperty("id").GetString().Should().Be("tooluse_YwZf4rS6VWyN2otOui0mEz");

        var fr = contents[2].GetProperty("parts").EnumerateArray()
            .First(p => p.TryGetProperty("functionResponse", out _))
            .GetProperty("functionResponse");
        fr.GetProperty("name").GetString().Should().Be("read_file");
        fr.GetProperty("id").GetString().Should().Be("tooluse_YwZf4rS6VWyN2otOui0mEz");
    }

    /// <summary>
    /// Same lost-name shape via OpenAI Chat: the <c>tool</c> role message only
    /// carries <c>tool_call_id</c>; the Gemini encoder must map it back to the
    /// function name from the assistant's <c>tool_calls</c>.
    /// </summary>
    [Fact]
    public void OpenAiChat_ToolMessage_To_Gemini_RestoresFunctionNameAndId()
    {
        var json = """
        {
          "model": "gpt-4o",
          "messages": [
            {"role": "user", "content": "what's the weather"},
            {"role": "assistant", "tool_calls": [
              {"id": "call_abc123", "type": "function", "function": {"name": "get_weather", "arguments": "{\"city\":\"SF\"}"}}
            ]},
            {"role": "tool", "tool_call_id": "call_abc123", "content": "sunny"}
          ]
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new GeminiOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);

        var fr = doc.RootElement.GetProperty("contents").EnumerateArray()
            .SelectMany(c => c.GetProperty("parts").EnumerateArray())
            .First(p => p.TryGetProperty("functionResponse", out _))
            .GetProperty("functionResponse");
        fr.GetProperty("name").GetString().Should().Be("get_weather");
        fr.GetProperty("id").GetString().Should().Be("call_abc123");
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
    /// Chat-shaped <c>response_format:{type:"json_object"}</c> routed to the
    /// Responses API must be upgraded to a permissive <c>json_schema</c>
    /// text.format when the input messages don't contain the word "json" —
    /// legacy JSON mode 400s upstream in that case, which cross-protocol
    /// callers routinely trip over.
    /// </summary>
    [Fact]
    public void OpenAiChat_ResponseFormatJsonObject_To_Responses_UpgradesToJsonSchema()
    {
        var json = """
        {
          "model": "gpt-5.5",
          "messages": [{"role": "user", "content": "hi"}],
          "response_format": {"type": "json_object"}
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var format = doc.RootElement.GetProperty("text").GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("strict").GetBoolean().Should().BeFalse();
        format.GetProperty("schema").GetProperty("type").GetString().Should().Be("object");
        format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// A compliant Chat-shaped <c>json_object</c> request — the word "json"
    /// appears in the input messages, so JSON mode is accepted upstream — must
    /// pass through as native <c>text.format:{type:"json_object"}</c> instead
    /// of being rewritten.
    /// </summary>
    [Fact]
    public void OpenAiChat_ResponseFormatJsonObject_WithJsonInMessages_To_Responses_PassesThrough()
    {
        var json = """
        {
          "model": "gpt-5.5",
          "messages": [{"role": "user", "content": "reply with a JSON object"}],
          "response_format": {"type": "json_object"}
        }
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var format = doc.RootElement.GetProperty("text").GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_object");
        format.TryGetProperty("schema", out _).Should().BeFalse();
    }

    /// <summary>
    /// Same upgrade for native Responses callers: a passthrough
    /// <c>text:{format:{type:"json_object"}, verbosity:"low"}</c> config swaps
    /// the format for the permissive json_schema while sibling fields survive.
    /// </summary>
    [Fact]
    public void OpenAiResponses_TextFormatJsonObject_RoundTrip_UpgradesToJsonSchema()
    {
        var json = """
        {
          "model": "gpt-5.5",
          "input": [{"role": "user", "content": [{"type": "input_text", "text": "hi"}]}],
          "text": {"format": {"type": "json_object"}, "verbosity": "low"}
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var text = doc.RootElement.GetProperty("text");
        text.GetProperty("verbosity").GetString().Should().Be("low");
        var format = text.GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("strict").GetBoolean().Should().BeFalse();
        format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// A compliant native Responses <c>json_object</c> request (input mentions
    /// "json") must survive the round trip untouched — the encoder only
    /// rewrites requests that would 400 upstream.
    /// </summary>
    [Fact]
    public void OpenAiResponses_TextFormatJsonObject_WithJsonInInput_RoundTrip_Unchanged()
    {
        var json = """
        {
          "model": "gpt-5.5",
          "input": [{"role": "user", "content": [{"type": "input_text", "text": "give me JSON"}]}],
          "text": {"format": {"type": "json_object"}, "verbosity": "low"}
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var text = doc.RootElement.GetProperty("text");
        text.GetProperty("verbosity").GetString().Should().Be("low");
        var format = text.GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_object");
        format.TryGetProperty("schema", out _).Should().BeFalse();
    }

    /// <summary>
    /// An explicit <c>json_schema</c> text.format must pass through untouched —
    /// the upgrade only targets legacy <c>json_object</c>.
    /// </summary>
    [Fact]
    public void OpenAiResponses_TextFormatJsonSchema_RoundTrip_Unchanged()
    {
        var json = """
        {
          "model": "gpt-5.5",
          "input": [{"role": "user", "content": [{"type": "input_text", "text": "hi"}]}],
          "text": {"format": {"type": "json_schema", "name": "animal", "strict": true,
                   "schema": {"type": "object", "properties": {"name": {"type": "string"}}}}}
        }
        """;
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var encoded = new OpenAiResponsesOutboundEncoder().Encode(transit);
        using var doc = JsonDocument.Parse(encoded);
        var format = doc.RootElement.GetProperty("text").GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("name").GetString().Should().Be("animal");
        format.GetProperty("strict").GetBoolean().Should().BeTrue();
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
