using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Hints;
using Microsoft.Extensions.AI;

namespace ChatTransit.Tests.Decoders;

public class OpenAiChatDecoderTests
{
    private readonly OpenAiChatInboundDecoder _decoder = new();

    [Fact]
    public void Decode_Simple_ReturnsCorrectModelAndStream()
    {
        var bytes = FixtureLoader.LoadBytes("openai_chat_simple.json");
        var result = _decoder.Decode(bytes);

        result.Model.Should().Be("gpt-4o");
        result.Stream.Should().BeTrue();
        result.Options.Temperature.Should().BeApproximately(0.7f, 0.001f);
        result.Options.MaxOutputTokens.Should().Be(2048);
    }

    [Fact]
    public void Decode_Simple_ParsesSystemAndUserMessages()
    {
        var bytes = FixtureLoader.LoadBytes("openai_chat_simple.json");
        var result = _decoder.Decode(bytes);

        result.Messages.Should().HaveCount(2);
        result.Messages[0].Role.Should().Be(ChatRole.System);
        result.Messages[0].Contents.OfType<TextContent>().First().Text
            .Should().Be("You are a helpful assistant.");
        result.Messages[1].Role.Should().Be(ChatRole.User);
        result.Messages[1].Contents.OfType<TextContent>().First().Text
            .Should().Be("What is the capital of France?");
    }

    [Fact]
    public void Decode_ToolCalls_ParsesFunctionCallAndResult()
    {
        var bytes = FixtureLoader.LoadBytes("openai_chat_tool_calls.json");
        var result = _decoder.Decode(bytes);

        // 3 messages: user, assistant with tool_call, tool result
        result.Messages.Should().HaveCount(3);

        var assistantMsg = result.Messages[1];
        assistantMsg.Role.Should().Be(ChatRole.Assistant);
        var fnCall = assistantMsg.Contents.OfType<FunctionCallContent>().First();
        fnCall.CallId.Should().Be("call_abc123");
        fnCall.Name.Should().Be("get_weather");
        fnCall.Arguments.Should().ContainKey("location");
        fnCall.Arguments!["location"]!.ToString().Should().Contain("Paris");

        var toolMsg = result.Messages[2];
        toolMsg.Role.Should().Be(ChatRole.Tool);
        var fnResult = toolMsg.Contents.OfType<FunctionResultContent>().First();
        fnResult.CallId.Should().Be("call_abc123");
        fnResult.Result!.ToString().Should().Contain("temperature");
    }

    [Fact]
    public void Decode_ToolCalls_ParsesToolDefinitions()
    {
        var bytes = FixtureLoader.LoadBytes("openai_chat_tool_calls.json");
        var result = _decoder.Decode(bytes);

        result.FunctionTools.Should().HaveCount(1);
        var tool = result.FunctionTools![0];
        tool.Name.Should().Be("get_weather");
        tool.Description.Should().Contain("weather");
        tool.ParametersSchema.Should().NotBeNull();
    }

    [Fact]
    public void Decode_Multimodal_ParsesImageContent()
    {
        var bytes = FixtureLoader.LoadBytes("openai_chat_multimodal.json");
        var result = _decoder.Decode(bytes);

        result.Messages.Should().HaveCount(1);
        var msg = result.Messages[0];
        msg.Role.Should().Be(ChatRole.User);
        msg.Contents.Should().HaveCount(2);
        msg.Contents[0].Should().BeOfType<TextContent>();
        msg.Contents[1].Should().BeAssignableTo<AIContent>(); // DataContent
    }

    [Fact]
    public void Decode_PreservesResponseFormatHint()
    {
        var json = """{"model":"gpt-4o","stream":false,"messages":[{"role":"user","content":"hi"}],"response_format":{"type":"json_object"}}""";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var result = _decoder.Decode(bytes);

        result.Hints.Should().ContainKey(OpenAiHints.ResponseFormat);
    }
}
