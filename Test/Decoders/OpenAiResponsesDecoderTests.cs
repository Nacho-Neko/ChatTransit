using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Hints;
using Microsoft.Extensions.AI;

namespace ChatTransit.Tests.Decoders;

public class OpenAiResponsesDecoderTests
{
    private readonly OpenAiResponsesInboundDecoder _decoder = new();

    [Fact]
    public void Decode_Simple_StringInput_ParsesAsUserMessage()
    {
        var bytes = FixtureLoader.LoadBytes("openai_responses_simple.json");
        var result = _decoder.Decode(bytes);

        result.Model.Should().Be("gpt-4o");
        result.Stream.Should().BeFalse();

        // system (from instructions) + user message
        result.Messages.Should().HaveCount(2);
        result.Messages[0].Role.Should().Be(ChatRole.System);
        result.Messages[0].Contents.OfType<TextContent>().First().Text
            .Should().Be("You are a helpful assistant.");
        result.Messages[1].Role.Should().Be(ChatRole.User);
        result.Messages[1].Contents.OfType<TextContent>().First().Text
            .Should().Be("What is the capital of Germany?");
    }

    [Fact]
    public void Decode_Simple_InstructionsStoredInHints()
    {
        var bytes = FixtureLoader.LoadBytes("openai_responses_simple.json");
        var result = _decoder.Decode(bytes);

        result.Hints.Should().ContainKey(OpenAiHints.ResponsesInstructions);
    }

    [Fact]
    public void Decode_ToolCalls_ParsesFunctionCallAndOutput()
    {
        var bytes = FixtureLoader.LoadBytes("openai_responses_tool_calls.json");
        var result = _decoder.Decode(bytes);

        // user, function_call (assistant), function_call_output (tool)
        result.Messages.Should().HaveCount(3);

        var fnCallMsg = result.Messages[1];
        fnCallMsg.Role.Should().Be(ChatRole.Assistant);
        var fnCall = fnCallMsg.Contents.OfType<FunctionCallContent>().First();
        fnCall.CallId.Should().Be("call_xyz789");
        fnCall.Name.Should().Be("get_stock_price");

        var fnResultMsg = result.Messages[2];
        fnResultMsg.Role.Should().Be(ChatRole.Tool);
        var fnResult = fnResultMsg.Contents.OfType<FunctionResultContent>().First();
        fnResult.CallId.Should().Be("call_xyz789");
        fnResult.Result!.ToString().Should().Contain("price");
    }

    [Fact]
    public void Decode_ToolCalls_ParsesToolDefinitions()
    {
        var bytes = FixtureLoader.LoadBytes("openai_responses_tool_calls.json");
        var result = _decoder.Decode(bytes);

        result.FunctionTools.Should().HaveCount(1);
        var tool = result.FunctionTools![0];
        tool.Name.Should().Be("get_stock_price");
        tool.Description.Should().Contain("stock");
    }

    [Fact]
    public void Decode_ArrayInput_MultipleMessages()
    {
        var json = """
        {
            "model": "gpt-4o",
            "stream": false,
            "input": [
                { "type": "message", "role": "user", "content": "Hello" },
                { "type": "message", "role": "assistant", "content": "Hi there!" },
                { "type": "message", "role": "user", "content": "How are you?" }
            ]
        }
        """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var result = _decoder.Decode(bytes);

        result.Messages.Should().HaveCount(3);
        result.Messages[0].Role.Should().Be(ChatRole.User);
        result.Messages[1].Role.Should().Be(ChatRole.Assistant);
        result.Messages[2].Role.Should().Be(ChatRole.User);
    }
}
