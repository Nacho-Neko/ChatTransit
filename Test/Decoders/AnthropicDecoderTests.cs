using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Hints;
using Microsoft.Extensions.AI;

namespace ChatTransit.Tests.Decoders;

public class AnthropicDecoderTests
{
    private readonly AnthropicInboundDecoder _decoder = new();

    [Fact]
    public void Decode_Simple_ReturnsCorrectModelAndStream()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_simple.json");
        var result = _decoder.Decode(bytes);

        result.Model.Should().Be("claude-opus-4-7");
        result.Stream.Should().BeTrue();
        result.Options.MaxOutputTokens.Should().Be(1024);
    }

    [Fact]
    public void Decode_Simple_InjectsSystemAsFirstMessage()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_simple.json");
        var result = _decoder.Decode(bytes);

        result.Messages.Should().HaveCount(2);
        result.Messages[0].Role.Should().Be(ChatRole.System);
        result.Messages[0].Contents.OfType<TextContent>().First().Text
            .Should().Be("You are a helpful assistant.");
        result.Messages[1].Role.Should().Be(ChatRole.User);
    }

    [Fact]
    public void Decode_ToolCalls_ParsesToolUseAndToolResult()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_tool_calls.json");
        var result = _decoder.Decode(bytes);

        // 3 messages: user, assistant (tool_use), user (tool_result)
        result.Messages.Should().HaveCount(3);

        var assistantMsg = result.Messages[1];
        assistantMsg.Role.Should().Be(ChatRole.Assistant);
        var fnCall = assistantMsg.Contents.OfType<FunctionCallContent>().First();
        fnCall.CallId.Should().Be("toolu_01A09q90qw90lq917835lq9");
        fnCall.Name.Should().Be("web_search");
        fnCall.Arguments.Should().ContainKey("query");

        var toolResultMsg = result.Messages[2];
        toolResultMsg.Role.Should().Be(ChatRole.User); // Anthropic tool_result is user-role
        var fnResult = toolResultMsg.Contents.OfType<FunctionResultContent>().First();
        fnResult.CallId.Should().Be("toolu_01A09q90qw90lq917835lq9");
        fnResult.Result!.ToString().Should().Contain("headlines");
    }

    [Fact]
    public void Decode_ToolCalls_ParsesToolDefinitions()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_tool_calls.json");
        var result = _decoder.Decode(bytes);

        result.FunctionTools.Should().HaveCount(1);
        var tool = result.FunctionTools![0];
        tool.Name.Should().Be("web_search");
        tool.Description.Should().Contain("Search");
        tool.ParametersSchema.Should().NotBeNull();
    }

    [Fact]
    public void Decode_Thinking_SetsIsThinkingHint()
    {
        var bytes = FixtureLoader.LoadBytes("anthropic_thinking.json");
        var result = _decoder.Decode(bytes);

        result.Hints.Should().ContainKey(AnthropicHints.IsThinkingModel);
        result.Hints[AnthropicHints.IsThinkingModel].Should().Be(true);
    }

    [Fact]
    public void Decode_ArraySystemPrompt_FlattensToSystemMessage()
    {
        var json = """
        {
            "model": "claude-opus-4-7",
            "max_tokens": 1024,
            "stream": false,
            "system": [
                {"type":"text","text":"You are helpful."},
                {"type":"text","text":"Be concise."}
            ],
            "messages": [{"role":"user","content":"Hi"}]
        }
        """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var result = _decoder.Decode(bytes);

        var sys = result.Messages.First(m => m.Role == ChatRole.System);
        sys.Contents.OfType<TextContent>().First().Text
            .Should().Contain("You are helpful.");
    }
}
