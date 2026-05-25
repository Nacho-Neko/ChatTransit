using Microsoft.Extensions.AI;

namespace Gateway.Shared.ChatTransit.Mapping;

/// <summary>
/// Handles the divergent ways each protocol represents system prompts:
///   OpenAI Chat: messages[{role:"system", content:"..."}] at the front
///   OpenAI Responses: top-level "instructions" string
///   Anthropic: top-level "system" field (string or content-block array)
///   Gemini: top-level "systemInstruction" content object
/// All normalise to one or more <see cref="ChatMessage"/> with
/// <see cref="ChatRole.System"/> role in the MEAI IR.
/// </summary>
public static class SystemMessageMapper
{
    /// <summary>
    /// Extracts system messages from <paramref name="messages"/> (index 0 system role)
    /// or from a separate <paramref name="systemText"/> value, returning (systemMessages, nonSystemMessages).
    /// </summary>
    public static (IList<ChatMessage> SystemMessages, IList<ChatMessage> UserMessages)
        Split(IList<ChatMessage> messages, string? systemText = null)
    {
        var system = new List<ChatMessage>();
        var other = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemText))
            system.Add(new ChatMessage(ChatRole.System, systemText));

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System)
                system.Add(m);
            else
                other.Add(m);
        }

        return (system, other);
    }

    /// <summary>
    /// Concatenates all system messages into a single string, joining with "\n\n".
    /// Returns null if there are no system messages.
    /// </summary>
    public static string? Flatten(IList<ChatMessage> allMessages)
    {
        var parts = allMessages
            .Where(m => m.Role == ChatRole.System)
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>
    /// Prepends any system messages from <paramref name="system"/> as role=system ChatMessages
    /// to the front of the returned list (non-system messages follow).
    /// </summary>
    public static IList<ChatMessage> Merge(IList<ChatMessage>? system, IList<ChatMessage> others)
    {
        if (system is null or { Count: 0 }) return others;
        var result = new List<ChatMessage>(system.Count + others.Count);
        result.AddRange(system);
        result.AddRange(others);
        return result;
    }
}
