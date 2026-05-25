using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Hints;
using Microsoft.Extensions.AI;

namespace ChatTransit.Tests.Decoders;

public class GeminiDecoderTests
{
    private readonly GeminiInboundDecoder _decoder = new();

    [Fact]
    public void Decode_Simple_ParsesContents()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_simple.json");
        var result = _decoder.Decode(bytes);

        result.Messages.Should().HaveCount(1);
        result.Messages[0].Role.Should().Be(ChatRole.User);
        result.Messages[0].Contents.OfType<TextContent>().First().Text
            .Should().Be("Tell me a short joke.");
    }

    [Fact]
    public void Decode_Simple_ParsesGenerationConfig()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_simple.json");
        var result = _decoder.Decode(bytes);

        result.Options.Temperature.Should().BeApproximately(0.9f, 0.001f);
        result.Options.MaxOutputTokens.Should().Be(512);
    }

    [Fact]
    public void Decode_ModelIsEmpty_GeminiUsesPathNotBody()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_simple.json");
        var result = _decoder.Decode(bytes);

        // Model comes from URL path, not body
        result.Model.Should().BeEmpty();
    }

    [Fact]
    public void Decode_ToolCalls_ParsesFunctionCallAndResponse()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_tool_calls.json");
        var result = _decoder.Decode(bytes);

        // user, model (functionCall), user (functionResponse)
        result.Messages.Should().HaveCount(3);

        var modelMsg = result.Messages[1];
        modelMsg.Role.Should().Be(ChatRole.Assistant);
        var fnCall = modelMsg.Contents.OfType<FunctionCallContent>().First();
        fnCall.Name.Should().Be("get_weather");

        var fnResponseMsg = result.Messages[2];
        fnResponseMsg.Role.Should().Be(ChatRole.User);
        var fnResult = fnResponseMsg.Contents.OfType<FunctionResultContent>().First();
        fnResult.CallId.Should().Be("get_weather");
        fnResult.Result!.ToString().Should().Contain("temperature");
    }

    [Fact]
    public void Decode_ToolCalls_ParsesToolDefinitions()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_tool_calls.json");
        var result = _decoder.Decode(bytes);

        result.FunctionTools.Should().HaveCount(1);
        var tool = result.FunctionTools![0];
        tool.Name.Should().Be("get_weather");
        tool.Description.Should().Contain("weather");
    }

    [Fact]
    public void Decode_Multimodal_ParsesSystemInstructionAndInlineData()
    {
        var bytes = FixtureLoader.LoadBytes("gemini_multimodal.json");
        var result = _decoder.Decode(bytes);

        // systemInstruction + 1 user message
        result.Messages.Should().HaveCount(2);
        result.Messages[0].Role.Should().Be(ChatRole.System);
        result.Messages[0].Contents.OfType<TextContent>().First().Text
            .Should().Be("You are a vision expert.");

        var userMsg = result.Messages[1];
        userMsg.Contents.Should().HaveCount(2);
        userMsg.Contents[0].Should().BeOfType<TextContent>();
        userMsg.Contents[1].Should().BeAssignableTo<DataContent>();
    }

    [Fact]
    public void Decode_SafetySettings_PreservedInHints()
    {
        var json = """
        {
            "contents": [{"role":"user","parts":[{"text":"hi"}]}],
            "safetySettings": [
                {"category":"HARM_CATEGORY_HATE_SPEECH","threshold":"BLOCK_ONLY_HIGH"}
            ]
        }
        """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var result = _decoder.Decode(bytes);

        result.Hints.Should().ContainKey(GeminiHints.SafetySettings);
    }

    [Fact]
    public void Decode_ThinkingBudget_PreservedInHints()
    {
        var json = """
        {
            "contents": [{"role":"user","parts":[{"text":"think hard"}]}],
            "generationConfig": {
                "thinkingConfig": { "thinkingBudget": 8192 }
            }
        }
        """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var result = _decoder.Decode(bytes);

        result.Hints.Should().ContainKey(GeminiHints.ThinkingBudget);
        result.Hints[GeminiHints.ThinkingBudget].Should().Be(8192);
    }
}
