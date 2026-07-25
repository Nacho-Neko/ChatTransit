using ChatTransit.Inbound;
using ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Outbound;

/// <summary>
/// <see cref="AnthropicOutboundEncoder"/> must never drop a turn on the floor.
///
/// <para>Anthropic validates the <i>shape</i> of <c>messages</c>: roles have to
/// alternate, and the newest Opus models reject a conversation ending on an
/// assistant turn (<c>"This model does not support assistant message prefill."</c>).
/// A message that maps to zero blocks — empty text, a content type with no block
/// shape — used to vanish here, so a transcoder bug could silently collapse two
/// user turns into one or leave an assistant turn last. These tests pin the role
/// sequence so that regression cannot come back.</para>
/// </summary>
public class AnthropicTurnPreservationTests
{
    private static JsonElement Encode(string openAiChatJson)
    {
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(openAiChatJson));
        var encoded = new AnthropicOutboundEncoder().Encode(transit);
        return JsonDocument.Parse(encoded).RootElement.Clone();
    }

    private static string RoleSequence(JsonElement root) =>
        string.Join(",", root.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()));

    [Fact]
    public void KeepsFinalUserTurn_WhenItsContentIsEmpty()
    {
        // The regression: an empty final user message used to disappear, leaving
        // the assistant turn last and a NoPrefill model rejecting the request.
        var root = Encode("""
        {"model":"gpt-4o","messages":[
          {"role":"user","content":"hi"},
          {"role":"assistant","content":"Hello! How can I help?"},
          {"role":"user","content":""}
        ]}
        """);

        RoleSequence(root).Should().Be("user,assistant,user",
            "an empty turn still owns its role slot — dropping it ends the request on an assistant turn");
    }

    [Fact]
    public void EmptyTurn_CarriesTheMinimalPlaceholder_BecauseAnthropicRejectsEmptyContent()
    {
        var root = Encode("""
        {"model":"gpt-4o","messages":[{"role":"user","content":""}]}
        """);

        // Unlike Gemini, an empty string is not a legal content here, so the
        // placeholder has to be non-empty — the same "." AnthropicChannel pads with.
        root.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be(".");
    }

    [Fact]
    public void PreservesAlternation_WhenAMiddleTurnIsEmpty()
    {
        var root = Encode("""
        {"model":"gpt-4o","messages":[
          {"role":"user","content":"first"},
          {"role":"assistant","content":""},
          {"role":"user","content":"second"}
        ]}
        """);

        RoleSequence(root).Should().Be("user,assistant,user",
            "dropping the empty assistant turn would leave two adjacent user turns");
    }

    [Fact]
    public void DoesNotAlterTurnsThatMapToRealBlocks()
    {
        var root = Encode("""
        {"model":"gpt-4o","messages":[
          {"role":"system","content":"Be brief."},
          {"role":"user","content":"hi"},
          {"role":"assistant","content":"Hello."},
          {"role":"user","content":"bye"}
        ]}
        """);

        RoleSequence(root).Should().Be("user,assistant,user",
            "the system message is hoisted to the system field, the rest are untouched");
        root.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("hi");
    }
}
