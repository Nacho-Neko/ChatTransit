using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Outbound;

/// <summary>
/// Encodes a <see cref="TransitRequest"/> into OpenAI Responses API JSON bytes.
/// <para>Faithfully restores reasoning items with their encrypted_content / summary
/// / item_id, replays server-side tool calls (file_search_call, web_search_call,
/// computer_call, …) that the decoder kept as passthrough, projects
/// <see cref="ChatToolMode"/> back to <c>tool_choice</c>, and forwards the full
/// Responses API surface (previous_response_id, store, include, truncation,
/// reasoning effort, response_format, etc.).</para>
/// </summary>
public sealed class OpenAiResponsesOutboundEncoder : IRequestEncoder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiResponses;

    public byte[] Encode(TransitRequest request)
    {
        var body = new Dictionary<string, object?>();

        body["model"] = request.Model;
        body["stream"] = request.Stream;

        // instructions: prefer explicit hint, fall back to system message text
        if (request.Hints.TryGetValue(OpenAiHints.ResponsesInstructions, out var insHint)
            && insHint is string insStr && !string.IsNullOrEmpty(insStr))
        {
            body["instructions"] = insStr;
        }
        else
        {
            var sysText = request.Messages
                .Where(m => m.Role == ChatRole.System)
                .SelectMany(m => m.Contents.OfType<TextContent>().Select(t => t.Text))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(sysText)) body["instructions"] = sysText;
        }

        var nonSystem = request.Messages.Where(m => m.Role != ChatRole.System).ToList();
        var passthrough = request.Hints.TryGetValue(OpenAiHints.ResponsesPassthroughItems, out var pt)
            ? pt as List<JsonElement>
            : null;
        body["input"] = BuildInput(nonSystem, passthrough);

        var opts = request.Options;
        if (opts.Temperature.HasValue)
            body["temperature"] = SamplingScaleMapper.ClampTemperatureForOpenAiScale(opts.Temperature.Value);
        if (opts.TopP.HasValue) body["top_p"] = SamplingScaleMapper.ClampTopP(opts.TopP.Value);
        if (opts.MaxOutputTokens.HasValue) body["max_output_tokens"] = opts.MaxOutputTokens.Value;
        // NOTE: Responses API (per https://platform.openai.com/docs/api-reference/responses/create)
        // does NOT accept frequency_penalty / presence_penalty / stop / seed —
        // forwarding them would 400 the upstream. They're silently dropped here
        // for cross-protocol routes (e.g. Anthropic → OpenAI Responses) that
        // carried the values through the IR.

        // tools = function tools + builtin (passthrough) tools
        var toolItems = new List<object>();
        if (request.FunctionTools is { Count: > 0 })
        {
            foreach (var t in request.FunctionTools)
            {
                toolItems.Add(new
                {
                    type = "function",
                    name = t.Name,
                    description = t.Description,
                    parameters = t.ParametersSchema ?? JsonSerializer.SerializeToElement(
                        new { type = "object", properties = new { } })
                });
            }
        }
        if (request.Hints.TryGetValue(OpenAiHints.ResponsesBuiltinTools, out var bt)
            && bt is List<JsonElement> btList)
        {
            foreach (var raw in btList)
                toolItems.Add(raw);
        }
        if (toolItems.Count > 0) body["tools"] = toolItems;

        // tool_choice
        if (request.Hints.TryGetValue(OpenAiHints.ToolChoice, out var tc) && tc is JsonElement tcEl)
        {
            body["tool_choice"] = tcEl;
        }
        else if (opts.ToolMode is { } toolMode)
        {
            var projected = ProjectToolMode(toolMode);
            if (projected != null) body["tool_choice"] = projected;
        }

        // Hint passthrough — everything else the Responses API supports
        if (request.Hints.TryGetValue(OpenAiHints.PreviousResponseId, out var prev) && prev is string prevStr)
            body["previous_response_id"] = prevStr;
        if (request.Hints.TryGetValue(OpenAiHints.ResponsesStore, out var store) && store is bool storeVal)
            body["store"] = storeVal;
        if (request.Hints.TryGetValue(OpenAiHints.ResponsesInclude, out var inc) && inc is JsonElement incEl)
            body["include"] = incEl;
        if (request.Hints.TryGetValue(OpenAiHints.ResponsesTruncation, out var tr) && tr is string trStr)
            body["truncation"] = trStr;
        if (request.Hints.TryGetValue(OpenAiHints.Reasoning, out var rs) && rs is JsonElement rsEl)
            body["reasoning"] = rsEl;
        // Responses API uses `text.format` (not `response_format`). Convert
        // OpenAI Chat-shaped response_format hints into the Responses shape so
        // Chat → Responses routes preserve JSON-mode/JSON-schema constraints.
        if (request.Hints.TryGetValue("openai.responses.text", out var tx) && tx is JsonElement txEl)
            body["text"] = txEl;
        else if (request.Hints.TryGetValue(OpenAiHints.ResponseFormat, out var rf) && rf is JsonElement rfEl
                 && rfEl.ValueKind == JsonValueKind.Object)
        {
            var textFormat = ConvertResponseFormatToTextFormat(rfEl);
            if (textFormat != null) body["text"] = new { format = textFormat };
        }
        if (request.Hints.TryGetValue(OpenAiHints.ParallelToolCalls, out var ptc) && ptc is bool ptcVal)
            body["parallel_tool_calls"] = ptcVal;
        if (request.Hints.TryGetValue(OpenAiHints.ServiceTier, out var st) && st is string stStr)
            body["service_tier"] = stStr;
        if (request.Hints.TryGetValue(OpenAiHints.User, out var u) && u is string uStr)
            body["user"] = uStr;
        if (request.Hints.TryGetValue(OpenAiHints.SafetyIdentifier, out var si) && si is string siStr)
            body["safety_identifier"] = siStr;
        if (request.Hints.TryGetValue(OpenAiHints.PromptCacheKey, out var pck) && pck is string pckStr)
            body["prompt_cache_key"] = pckStr;
        if (request.Hints.TryGetValue("openai.responses.metadata", out var md) && md is JsonElement mdEl)
            body["metadata"] = mdEl;

        return JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object? ProjectToolMode(ChatToolMode mode) => mode switch
    {
        NoneChatToolMode => (object)"none",
        AutoChatToolMode => (object)"auto",
        RequiredChatToolMode r when !string.IsNullOrEmpty(r.RequiredFunctionName)
            => new { type = "function", name = r.RequiredFunctionName },
        RequiredChatToolMode => (object)"required",
        _ => null
    };

    private static List<object> BuildInput(IList<ChatMessage> messages,
        List<JsonElement>? passthroughItems)
    {
        var result = new List<object>();

        // Splice passthrough items first — they typically represent server-side
        // tool invocations that occurred during a prior turn and need to come
        // alongside any matching message items. (The exact relative order to
        // messages is not strict per the API, but keeping passthrough at the
        // start mirrors what the model emitted.)
        if (passthroughItems != null)
        {
            foreach (var raw in passthroughItems)
                result.Add(raw);
        }

        foreach (var msg in messages)
        {
            // Tool results
            var fnResults = msg.Contents.OfType<FunctionResultContent>().ToList();
            if (fnResults.Count > 0)
            {
                foreach (var fr in fnResults)
                {
                    result.Add(new
                    {
                        type = "function_call_output",
                        call_id = fr.CallId,
                        output = SerializeToolResult(fr.Result)
                    });
                }
                continue;
            }

            // Reasoning items (must come before function_call / message in same turn)
            var thinkingItems = msg.Contents.Where(ThinkingMapper.IsThinkingContent).ToList();
            foreach (var th in thinkingItems)
                result.Add(BuildReasoningItem(th));

            // Function calls
            var fnCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
            if (fnCalls.Count > 0)
            {
                foreach (var fc in fnCalls)
                {
                    var fcItem = new Dictionary<string, object?>
                    {
                        ["type"] = "function_call",
                        ["call_id"] = fc.CallId,
                        ["name"] = fc.Name,
                        ["arguments"] = SerializeArgs(fc.Arguments)
                    };
                    if (fc.AdditionalProperties?.TryGetValue("transit.openai.item_id", out var iid) == true
                        && iid is string iidStr)
                        fcItem["id"] = iidStr;
                    result.Add(fcItem);
                }
                if (thinkingItems.Count == 0 && msg.Contents.OfType<TextContent>().All(t => string.IsNullOrEmpty(t.Text)))
                    continue;
            }

            // Plain message item (skip if it would be empty)
            var contentParts = BuildContentParts(msg.Contents, msg.Role);
            if (contentParts.Count > 0)
            {
                var role = msg.Role == ChatRole.Assistant ? "assistant" : "user";
                result.Add(new
                {
                    type = "message",
                    role,
                    content = contentParts
                });
            }
        }
        return result;
    }

    private static object BuildReasoningItem(AIContent content)
    {
        var item = new Dictionary<string, object?>
        {
            ["type"] = "reasoning"
        };
        var itemId = ThinkingMapper.GetOpenAiReasoningItemId(content);
        if (!string.IsNullOrEmpty(itemId)) item["id"] = itemId;

        // Preserve the original summary structure if available; otherwise synthesize
        // a single summary_text part from the thinking text.
        if (content.AdditionalProperties?.TryGetValue(ThinkingMapper.OpenAiReasoningSummary, out var sm) == true
            && sm is JsonElement smEl)
        {
            item["summary"] = smEl;
        }
        else
        {
            var text = ThinkingMapper.GetThinkingText(content) ?? "";
            item["summary"] = new[] { new { type = "summary_text", text } };
        }

        var enc = ThinkingMapper.GetOpenAiEncryptedContent(content);
        if (!string.IsNullOrEmpty(enc)) item["encrypted_content"] = enc;

        return item;
    }

    private static List<object> BuildContentParts(IList<AIContent> contents, ChatRole role)
    {
        var parts = new List<object>();
        foreach (var c in contents)
        {
            if (ThinkingMapper.IsThinkingContent(c)) continue;
            switch (c)
            {
                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                {
                    var textType = role == ChatRole.Assistant ? "output_text" : "input_text";
                    parts.Add(new { type = textType, text = tc.Text });
                    break;
                }

                case DataContent dc:
                {
                    var mime = dc.MediaType ?? "image/png";
                    if (mime.StartsWith("image/", StringComparison.Ordinal))
                    {
                        var url = MultimodalContentMapper.ToOpenAiImageUrl(dc);
                        if (url != null) parts.Add(new { type = "input_image", image_url = url });
                    }
                    else if (mime.StartsWith("audio/", StringComparison.Ordinal))
                    {
                        // OpenAI Responses also accepts input_audio (mirrors Chat Completions)
                        var fmt = mime.Substring(6);
                        var b64 = Convert.ToBase64String(dc.Data.ToArray());
                        parts.Add(new { type = "input_audio", input_audio = new { data = b64, format = fmt } });
                    }
                    else
                    {
                        var b64 = Convert.ToBase64String(dc.Data.ToArray());
                        var filename = dc.AdditionalProperties?
                            .TryGetValue("transit.openai.filename", out var fn) == true ? fn as string : null;
                        var inputFile = new Dictionary<string, object?>
                        {
                            ["type"] = "input_file",
                            ["file_data"] = b64
                        };
                        if (!string.IsNullOrEmpty(filename)) inputFile["filename"] = filename;
                        parts.Add(inputFile);
                    }
                    break;
                }

                case UriContent uc:
                {
                    if (uc.AdditionalProperties?.TryGetValue("transit.openai.file_id", out var fid) == true
                        && fid is string fileId)
                    {
                        parts.Add(new { type = "input_file", file_id = fileId });
                    }
                    else
                    {
                        var url = uc.Uri?.ToString();
                        if (string.IsNullOrEmpty(url)) break;
                        if ((uc.MediaType ?? "").StartsWith("image/", StringComparison.Ordinal))
                            parts.Add(new { type = "input_image", image_url = url });
                        else
                            parts.Add(new { type = "input_file", file_url = url });
                    }
                    break;
                }
            }
        }
        return parts;
    }

    /// <summary>
    /// Converts a Chat-Completions-shaped <c>response_format</c>
    /// (<c>{type:"json_schema", json_schema:{schema, name?, strict?}}</c> or
    /// <c>{type:"json_object"}</c>) into the Responses-API
    /// <c>text.format</c> shape (<c>{type:"json_schema", schema, name?, strict?}</c>
    /// or <c>{type:"json_object"}</c>). Returns null when the input shape is
    /// unrecognised; the caller falls back to omitting the field.
    /// </summary>
    private static object? ConvertResponseFormatToTextFormat(JsonElement rf)
    {
        var type = rf.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
        if (type == "json_object") return new { type = "json_object" };
        if (type == "json_schema"
            && rf.TryGetProperty("json_schema", out var js)
            && js.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object?> { ["type"] = "json_schema" };
            if (js.TryGetProperty("name", out var name) && name.GetString() is { } n)
                dict["name"] = n;
            if (js.TryGetProperty("schema", out var schema) && schema.ValueKind != JsonValueKind.Null)
                dict["schema"] = schema;
            if (js.TryGetProperty("strict", out var strict) && strict.ValueKind != JsonValueKind.Null)
                dict["strict"] = strict.ValueKind == JsonValueKind.True;
            if (js.TryGetProperty("description", out var desc) && desc.GetString() is { } d)
                dict["description"] = d;
            return dict;
        }
        return null;
    }

    private static object SerializeToolResult(object? result) => result switch
    {
        null => "",
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
        JsonElement je => je.GetRawText(),
        string s => s,
        _ => result.ToString() ?? ""
    };

    private static string SerializeArgs(IDictionary<string, object?>? args)
    {
        if (args is null) return "{}";
        try { return JsonSerializer.Serialize(args, JsonOpts); }
        catch { return "{}"; }
    }
}
