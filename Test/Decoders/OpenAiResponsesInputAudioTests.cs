using Gateway.Shared.ChatTransit.Inbound;
using Microsoft.Extensions.AI;
using System.Text;

namespace ChatTransit.Tests.Decoders;

/// <summary>
/// P1-8: the OpenAI Responses input union accepts <c>input_audio</c> parts on
/// user messages (same shape as Chat Completions). The decoder must turn them
/// into <see cref="DataContent"/> so cross-protocol routes preserve audio.
/// </summary>
public class OpenAiResponsesInputAudioTests
{
    [Fact]
    public void InputAudio_IsDecoded_As_DataContent()
    {
        // 4 bytes of audio: "test" base64-encoded.
        const string b64 = "dGVzdA==";
        var json = """
        {
          "model": "gpt-4o-audio-preview",
          "input": [
            {"role": "user", "content": [
              {"type": "input_audio", "input_audio": {"data": "__B64__", "format": "wav"}}
            ]}
          ]
        }
        """.Replace("__B64__", b64);
        var transit = new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var user = transit.Messages.First(m => m.Role == ChatRole.User);
        var audio = user.Contents.OfType<DataContent>().Single();
        audio.MediaType.Should().Be("audio/wav");
        audio.Data.ToArray().Should().Equal(Convert.FromBase64String(b64));
    }

    [Fact]
    public void InputAudio_MissingData_DoesNotThrow()
    {
        var json = """
        {
          "model": "gpt-4o-audio-preview",
          "input": [
            {"role": "user", "content": [
              {"type": "input_audio", "input_audio": {"format": "wav"}}
            ]}
          ]
        }
        """;
        var act = () => new OpenAiResponsesInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        act.Should().NotThrow();
    }
}
