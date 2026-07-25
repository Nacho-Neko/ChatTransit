using ChatTransit.Inbound;
using ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Outbound;

/// <summary>
/// <see cref="GeminiOutboundEncoder"/> must never drop a turn on the floor.
///
/// <para>Gemini validates the <i>shape</i> of <c>contents</c>: the last non-empty
/// turn may not be <c>model</c>, and Gemini 3+ answers one that is with
/// <c>400 "Requests ending with a model turn are not supported."</c> A message that
/// maps to zero parts — empty text, an image whose base64 the decoder rejected, a
/// content type with no part shape — used to vanish here, so a transcoder bug could
/// silently turn a valid conversation into a rejected prefill. These tests pin the
/// turn count so that regression cannot come back.</para>
/// </summary>
public class GeminiTurnPreservationTests
{
    private static JsonElement Encode(string openAiChatJson)
    {
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(openAiChatJson));
        var encoded = new GeminiOutboundEncoder().Encode(transit);
        return JsonDocument.Parse(encoded).RootElement.Clone();
    }

    private static string RoleSequence(JsonElement root) =>
        string.Join(",", root.GetProperty("contents").EnumerateArray()
            .Select(c => c.GetProperty("role").GetString()));

    [Fact]
    public void KeepsFinalUserTurn_WhenItsContentIsEmpty()
    {
        // The regression: an empty final user message used to disappear, leaving
        // the assistant turn last and the upstream rejecting the whole request.
        var root = Encode("""
        {"model":"gpt-4o","messages":[
          {"role":"user","content":"hi"},
          {"role":"assistant","content":"Hello! How can I help?"},
          {"role":"user","content":""}
        ]}
        """);

        RoleSequence(root).Should().Be("user,model,user",
            "an empty turn still owns its role slot — dropping it ends the request on a model turn");
    }

    [Fact]
    public void EmptyTurn_CarriesAnEmptyTextPart_RatherThanNoParts()
    {
        var root = Encode("""
        {"model":"gpt-4o","messages":[{"role":"user","content":""}]}
        """);

        var parts = root.GetProperty("contents")[0].GetProperty("parts");
        parts.GetArrayLength().Should().Be(1);
        parts[0].GetProperty("text").GetString().Should().Be("",
            "the placeholder must not invent prompt content the model would read");
    }

    [Fact]
    public void PreservesTurnCount_ForAConversationOfEmptyMessages()
    {
        var root = Encode("""
        {"model":"gpt-4o","messages":[
          {"role":"user","content":""},
          {"role":"assistant","content":""},
          {"role":"user","content":""}
        ]}
        """);

        RoleSequence(root).Should().Be("user,model,user");
    }

    [Fact]
    public void DoesNotAlterTurnsThatMapToRealParts()
    {
        var root = Encode("""
        {"model":"gpt-4o","messages":[
          {"role":"system","content":"Be brief."},
          {"role":"user","content":"hi"},
          {"role":"assistant","content":"Hello."},
          {"role":"user","content":"bye"}
        ]}
        """);

        RoleSequence(root).Should().Be("user,model,user",
            "the system message is hoisted to systemInstruction, the rest are untouched");
        root.GetProperty("contents")[0].GetProperty("parts")[0]
            .GetProperty("text").GetString().Should().Be("hi");
    }
}
