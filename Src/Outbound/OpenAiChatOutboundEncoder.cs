using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Outbound;

/// <summary>
/// Encodes a <see cref="TransitRequest"/> into OpenAI Chat Completions JSON bytes.
/// <para>Emits <c>reasoning_content</c> on assistant messages, projects MEAI
/// <see cref="ChatToolMode"/> back to OpenAI's <c>tool_choice</c>, and passes
/// through logit_bias, logprobs, n, user, prompt_cache_key, safety_identifier.</para>
/// </summary>
public sealed class OpenAiChatOutboundEncoder : IRequestEncoder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiChat;

    public byte[] Encode(TransitRequest request)
    {
        var body = new Dictionary<string, object?>();

        body["model"] = request.Model;
        body["stream"] = request.Stream;
        body["messages"] = BuildMessages(request.Messages);

        // OpenAI Chat is IR-native scale; we still defensively clamp in case an
        // upstream decoder produced something out-of-spec. top_k is intentionally
        // skipped (OpenAI has no such field).
        var opts = request.Options;
        if (opts.Temperature.HasValue)
            body["temperature"] = SamplingScaleMapper.ClampTemperatureForOpenAiScale(opts.Temperature.Value);
        if (opts.TopP.HasValue) body["top_p"] = SamplingScaleMapper.ClampTopP(opts.TopP.Value);
        if (opts.MaxOutputTokens.HasValue) body["max_tokens"] = opts.MaxOutputTokens.Value;
        if (opts.FrequencyPenalty.HasValue)
            body["frequency_penalty"] = Math.Clamp(opts.FrequencyPenalty.Value, -2f, 2f);
        if (opts.PresencePenalty.HasValue)
            body["presence_penalty"] = Math.Clamp(opts.PresencePenalty.Value, -2f, 2f);
        if (opts.Seed.HasValue) body["seed"] = opts.Seed.Value;

        if (opts.StopSequences is { Count: > 0 })
            body["stop"] = opts.StopSequences.Count == 1 ? (object)opts.StopSequences[0] : opts.StopSequences;

        // Tools
        if (request.FunctionTools is { Count: > 0 })
        {
            body["tools"] = request.FunctionTools.Select(t => (object)new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.ParametersSchema ?? JsonSerializer.SerializeToElement(
                        new { type = "object", properties = new { } })
                }
            }).ToList();
        }

        // tool_choice: prefer the raw OpenAI-shaped hint, otherwise project from IR
        if (request.Hints.TryGetValue(OpenAiHints.ToolChoice, out var tc) && tc is JsonElement tcEl)
        {
            body["tool_choice"] = tcEl;
        }
        else if (opts.ToolMode is { } toolMode)
        {
            var projected = ProjectToolMode(toolMode);
            if (projected != null) body["tool_choice"] = projected;
        }

        // Hint passthrough
        if (request.Hints.TryGetValue(OpenAiHints.ResponseFormat, out var rf) && rf is JsonElement rfEl)
            body["response_format"] = rfEl;
        if (request.Hints.TryGetValue(OpenAiHints.ParallelToolCalls, out var ptc) && ptc is bool ptcVal)
            body["parallel_tool_calls"] = ptcVal;
        if (request.Hints.TryGetValue(OpenAiHints.ServiceTier, out var st) && st is string stStr)
            body["service_tier"] = stStr;
        if (request.Hints.TryGetValue(OpenAiHints.LogitBias, out var lb) && lb is JsonElement lbEl)
            body["logit_bias"] = lbEl;
        if (request.Hints.TryGetValue(OpenAiHints.Logprobs, out var lp) && lp is true)
            body["logprobs"] = true;
        if (request.Hints.TryGetValue(OpenAiHints.TopLogprobs, out var tlp) && tlp is int tlpv)
            body["top_logprobs"] = tlpv;
        if (request.Hints.TryGetValue(OpenAiHints.CandidateCount, out var nh) && nh is int nv)
            body["n"] = nv;
        if (request.Hints.TryGetValue(OpenAiHints.User, out var u) && u is string uStr)
            body["user"] = uStr;
        if (request.Hints.TryGetValue(OpenAiHints.PromptCacheKey, out var pck) && pck is string pckStr)
            body["prompt_cache_key"] = pckStr;
        if (request.Hints.TryGetValue(OpenAiHints.SafetyIdentifier, out var si) && si is string siStr)
            body["safety_identifier"] = siStr;
        // Always request usage from the upstream on streaming calls — OpenAI only
        // emits the final usage chunk when stream_options.include_usage=true, and
        // the gateway needs it for metering regardless of the caller's preference.
        if (request.Stream)
            body["stream_options"] = new { include_usage = true };
        if (request.Hints.TryGetValue(OpenAiHints.ReasoningEffort, out var re) && re is string reStr
            && !string.IsNullOrEmpty(reStr))
            body["reasoning_effort"] = reStr;

        return JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object? ProjectToolMode(ChatToolMode mode) => mode switch
    {
        NoneChatToolMode => (object)"none",
        AutoChatToolMode => (object)"auto",
        RequiredChatToolMode r when !string.IsNullOrEmpty(r.RequiredFunctionName)
            => new { type = "function", function = new { name = r.RequiredFunctionName } },
        RequiredChatToolMode => (object)"required",
        _ => null
    };

    private static List<object> BuildMessages(IList<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                ChatRole r when r == ChatRole.System => "system",
                ChatRole r when r == ChatRole.Assistant => "assistant",
                ChatRole r when r == ChatRole.Tool => "tool",
                _ => "user"
            };

            // Tool result messages: one OpenAI message per FunctionResultContent
            var fnResults = msg.Contents.OfType<FunctionResultContent>().ToList();
            if (fnResults.Count > 0)
            {
                foreach (var fr in fnResults)
                {
                    result.Add(new
                    {
                        role = "tool",
                        tool_call_id = fr.CallId,
                        content = SerializeToolResult(fr.Result)
                    });
                }
                continue;
            }

            var fnCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
            var thinkingText = msg.Contents
                .Where(ThinkingMapper.IsThinkingContent)
                .OfType<TextContent>()
                .Select(t => t.Text)
                .FirstOrDefault(t => !string.IsNullOrEmpty(t));
            var textContent = msg.Contents.OfType<TextContent>()
                .Where(t => !ThinkingMapper.IsThinkingContent(t))
                .Select(t => t.Text)
                .FirstOrDefault();

            if (fnCalls.Count > 0)
            {
                var toolCalls = fnCalls.Select(fc => (object)new
                {
                    id = fc.CallId,
                    type = "function",
                    function = new { name = fc.Name, arguments = SerializeArgs(fc.Arguments) }
                }).ToList();
                var assistantMsg = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = textContent,
                    ["tool_calls"] = toolCalls
                };
                if (!string.IsNullOrEmpty(thinkingText))
                    assistantMsg["reasoning_content"] = thinkingText;
                result.Add(assistantMsg);
                continue;
            }

            // Text/multimodal message
            var contentParts = BuildContentParts(msg.Contents);

            // Pure text shorthand
            if (contentParts.Count == 1 && contentParts[0] is string simpleText)
            {
                var simple = new Dictionary<string, object?> { ["role"] = role, ["content"] = simpleText };
                if (role == "assistant" && !string.IsNullOrEmpty(thinkingText))
                    simple["reasoning_content"] = thinkingText;
                result.Add(simple);
                continue;
            }

            if (contentParts.Count > 0)
            {
                var rich = new Dictionary<string, object?> { ["role"] = role, ["content"] = contentParts };
                if (role == "assistant" && !string.IsNullOrEmpty(thinkingText))
                    rich["reasoning_content"] = thinkingText;
                result.Add(rich);
            }
            else
            {
                var empty = new Dictionary<string, object?> { ["role"] = role, ["content"] = "" };
                if (role == "assistant" && !string.IsNullOrEmpty(thinkingText))
                    empty["reasoning_content"] = thinkingText;
                result.Add(empty);
            }
        }
        return result;
    }

    private static object SerializeToolResult(object? result)
    {
        switch (result)
        {
            case null:
                return "";
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                return je.GetString() ?? "";
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                // Anthropic's tool_result.content array can contain text + image
                // blocks. OpenAI Chat tool messages accept an array of
                // {type:"text"|"image_url",...} parts since 2024-10 — translate
                // each block so cross-protocol routes don't drop images.
                return ConvertAnthropicToolResultArray(je);
            case JsonElement je:
                return je.GetRawText();
            case string s:
                return s;
            default:
                return result.ToString() ?? "";
        }
    }

    private static object ConvertAnthropicToolResultArray(JsonElement arr)
    {
        var parts = new List<object>();
        foreach (var block in arr.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var t = block.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            switch (t)
            {
                case "text":
                    if (block.TryGetProperty("text", out var tx) && tx.GetString() is { Length: > 0 } txt)
                        parts.Add(new { type = "text", text = txt });
                    break;
                case "image":
                    // Anthropic image → OpenAI image_url (data URI for base64,
                    // pass-through for url).
                    if (block.TryGetProperty("source", out var src))
                    {
                        var srcType = src.TryGetProperty("type", out var st) ? st.GetString() : null;
                        if (srcType == "base64"
                            && src.TryGetProperty("media_type", out var mt)
                            && src.TryGetProperty("data", out var d)
                            && d.GetString() is { Length: > 0 } b64)
                        {
                            var mime = mt.GetString() ?? "image/png";
                            parts.Add(new
                            {
                                type = "image_url",
                                image_url = new { url = $"data:{mime};base64,{b64}" }
                            });
                        }
                        else if (srcType == "url"
                                 && src.TryGetProperty("url", out var u)
                                 && u.GetString() is { Length: > 0 } urlStr)
                        {
                            parts.Add(new { type = "image_url", image_url = new { url = urlStr } });
                        }
                    }
                    break;
            }
        }
        return parts.Count > 0 ? parts : (object)arr.GetRawText();
    }

    private static List<object> BuildContentParts(IList<AIContent> contents)
    {
        var parts = new List<object>();
        foreach (var c in contents)
        {
            // Thinking content is emitted at the message level via reasoning_content,
            // not as a content part.
            if (ThinkingMapper.IsThinkingContent(c)) continue;

            switch (c)
            {
                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    parts.Add(tc.Text!);
                    break;

                case DataContent dc:
                    var mime = dc.MediaType ?? "image/png";
                    if (mime.StartsWith("image/", StringComparison.Ordinal))
                    {
                        var url = MultimodalContentMapper.ToOpenAiImageUrl(dc);
                        if (url != null) parts.Add(new { type = "image_url", image_url = new { url } });
                    }
                    else if (mime.StartsWith("audio/", StringComparison.Ordinal))
                    {
                        var fmt = mime.Substring(6);
                        var b64 = Convert.ToBase64String(dc.Data.ToArray());
                        parts.Add(new { type = "input_audio", input_audio = new { data = b64, format = fmt } });
                    }
                    else if (mime == "application/pdf" || mime.StartsWith("application/", StringComparison.Ordinal))
                    {
                        var b64 = Convert.ToBase64String(dc.Data.ToArray());
                        var filename = dc.AdditionalProperties?
                            .TryGetValue("transit.openai.filename", out var fn) == true ? fn as string : null;
                        var file = new Dictionary<string, object?> { ["file_data"] = b64 };
                        if (!string.IsNullOrEmpty(filename)) file["filename"] = filename;
                        parts.Add(new { type = "file", file });
                    }
                    break;

                case UriContent uc:
                    if (uc.AdditionalProperties?.TryGetValue("transit.openai.file_id", out var fid) == true
                        && fid is string fileId)
                    {
                        parts.Add(new { type = "file", file = new { file_id = fileId } });
                    }
                    else
                    {
                        var imgUrl = MultimodalContentMapper.ToOpenAiImageUrl(uc);
                        if (imgUrl != null) parts.Add(new { type = "image_url", image_url = new { url = imgUrl } });
                    }
                    break;
            }
        }
        return parts;
    }

    private static string SerializeArgs(IDictionary<string, object?>? args)
    {
        if (args is null) return "{}";
        try { return JsonSerializer.Serialize(args, JsonOpts); }
        catch { return "{}"; }
    }
}
