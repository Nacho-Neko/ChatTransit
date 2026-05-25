using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Inbound;

/// <summary>
/// Decodes OpenAI Responses API (<c>POST /v1/responses</c>) JSON into a <see cref="TransitRequest"/>.
/// <para>Fully covers the official input-item union: <c>message</c>, <c>function_call</c>,
/// <c>function_call_output</c>, <c>reasoning</c> (with <c>encrypted_content</c> and
/// <c>summary</c>), plus server-side tool items
/// (<c>file_search_call</c>, <c>web_search_call</c>, <c>computer_call</c>,
/// <c>computer_call_output</c>, <c>code_interpreter_call</c>,
/// <c>image_generation_call</c>, <c>mcp_call</c>, <c>mcp_approval_request</c>,
/// <c>mcp_approval_response</c>, <c>shell_call</c>, <c>shell_call_output</c>,
/// <c>apply_patch_call</c>, <c>apply_patch_call_output</c>,
/// <c>custom_tool_call</c>, <c>custom_tool_call_output</c>,
/// <c>item_reference</c>) which are kept verbatim in
/// <see cref="OpenAiHints.ResponsesPassthroughItems"/> for round-trip fidelity.</para>
/// </summary>
public sealed class OpenAiResponsesInboundDecoder : IRequestDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiResponses;

    private static readonly HashSet<string> PassthroughItemTypes = new(StringComparer.Ordinal)
    {
        "file_search_call",
        "web_search_call",
        "computer_call",
        "computer_call_output",
        "code_interpreter_call",
        "image_generation_call",
        "mcp_list_tools",
        "mcp_call",
        "mcp_approval_request",
        "mcp_approval_response",
        "shell_call",
        "shell_call_output",
        "local_shell_call",
        "local_shell_call_output",
        "apply_patch_call",
        "apply_patch_call_output",
        "tool_search_call",
        "tool_search_output",
        "custom_tool_call",
        "custom_tool_call_output",
        "compaction",
        "item_reference",
    };

    public TransitRequest Decode(byte[] requestBytes, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(requestBytes);
        var root = doc.RootElement;

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        var stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;

        var instructions = root.TryGetProperty("instructions", out var ins) ? ins.GetString() : null;

        var hints = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(instructions))
            hints[OpenAiHints.ResponsesInstructions] = instructions;

        var messages = DecodeInput(root, instructions, hints);
        var options = new ChatOptions();
        ApplyScalars(root, options);
        ApplyToolChoice(root, options, hints);
        BuildHints(root, hints);
        var tools = DecodeTools(root, hints);

        return new TransitRequest
        {
            Messages = messages,
            Options = options,
            Hints = hints,
            Model = model,
            Stream = stream,
            FunctionTools = tools
        };
    }

    // ── Input decoding ────────────────────────────────────────────────────────

    private static IList<ChatMessage> DecodeInput(JsonElement root, string? instructions,
        Dictionary<string, object?> hints)
    {
        var result = new List<ChatMessage>();
        var passthrough = new List<JsonElement>();

        if (!string.IsNullOrWhiteSpace(instructions))
            result.Add(new ChatMessage(ChatRole.System, instructions));

        if (!root.TryGetProperty("input", out var input))
            return result;

        if (input.ValueKind == JsonValueKind.String)
        {
            var text = input.GetString() ?? "";
            if (text.Length > 0)
                result.Add(new ChatMessage(ChatRole.User, text));
            return result;
        }

        if (input.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in input.EnumerateArray())
        {
            var itemType = item.TryGetProperty("type", out var t) ? t.GetString() ?? "message" : "message";

            // Messages with no "type" still default to message
            if (string.IsNullOrEmpty(itemType)) itemType = "message";

            if (itemType == "message")
            {
                var roleStr = item.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                var role = roleStr switch
                {
                    "system" or "developer" => ChatRole.System,
                    "assistant" => ChatRole.Assistant,
                    _ => ChatRole.User
                };
                var contents = DecodeMessageContent(item);
                result.Add(new ChatMessage(role, contents));
            }
            else if (itemType == "function_call")
            {
                var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var args = item.TryGetProperty("arguments", out var a) ? a.GetString() ?? "" : "";
                var parsed = ParseArguments(args);
                var fcc = new FunctionCallContent(callId, name, parsed);
                if (item.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id)
                {
                    fcc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    fcc.AdditionalProperties["transit.openai.item_id"] = id;
                }
                result.Add(new ChatMessage(ChatRole.Assistant, [fcc]));
            }
            else if (itemType == "function_call_output")
            {
                var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
                object? output = null;
                if (item.TryGetProperty("output", out var o))
                {
                    output = o.ValueKind switch
                    {
                        JsonValueKind.String => o.GetString() ?? "",
                        JsonValueKind.Array or JsonValueKind.Object => (object)o.Clone(),
                        _ => o.ToString()
                    };
                }
                result.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, output)]));
            }
            else if (itemType == "reasoning")
            {
                // Responses API reasoning items: { id, type:"reasoning", summary:[...], encrypted_content?:"..." }
                var summary = item.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.Array
                    ? sumEl.Clone() : (JsonElement?)null;
                var encrypted = item.TryGetProperty("encrypted_content", out var ec) ? ec.GetString() : null;
                var itemId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                // Render the summary as readable text (concatenated summary parts)
                var sb = new System.Text.StringBuilder();
                if (summary.HasValue && summary.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in summary.Value.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var ptxt)
                            && ptxt.GetString() is { Length: > 0 } pt)
                        {
                            if (sb.Length > 0) sb.AppendLine();
                            sb.Append(pt);
                        }
                    }
                }
                var thinking = ThinkingMapper.CreateThinkingContent(
                    sb.ToString(), openAiEncryptedContent: encrypted);
                if (!string.IsNullOrEmpty(itemId))
                    ThinkingMapper.SetOpenAiReasoningItemId(thinking, itemId);
                if (summary.HasValue)
                {
                    thinking.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    thinking.AdditionalProperties[ThinkingMapper.OpenAiReasoningSummary] = summary.Value;
                }
                result.Add(new ChatMessage(ChatRole.Assistant, [thinking]));
            }
            else if (PassthroughItemTypes.Contains(itemType))
            {
                // Preserve raw — outbound encoder splices these back in their original positions
                passthrough.Add(item.Clone());
            }
            else
            {
                // Unknown future item type — still preserve verbatim
                passthrough.Add(item.Clone());
            }
        }

        if (passthrough.Count > 0)
            hints[OpenAiHints.ResponsesPassthroughItems] = passthrough;

        return result;
    }

    private static List<AIContent> DecodeMessageContent(JsonElement item)
    {
        var contents = new List<AIContent>();
        if (!item.TryGetProperty("content", out var content)) return contents;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? "";
            if (text.Length > 0) contents.Add(new TextContent(text));
            return contents;
        }

        if (content.ValueKind != JsonValueKind.Array) return contents;

        foreach (var part in content.EnumerateArray())
        {
            var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() ?? "" : "";
            switch (partType)
            {
                case "input_text" or "output_text" or "text":
                    if (part.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } t)
                        contents.Add(new TextContent(t));
                    break;

                case "input_image":
                {
                    string? url = null;
                    if (part.TryGetProperty("image_url", out var iu) && iu.GetString() is { Length: > 0 } iuv)
                        url = iuv;
                    else if (part.TryGetProperty("file_id", out var fid) && fid.GetString() is { Length: > 0 } fidv)
                        url = $"openai-file://{fidv}";
                    if (url != null)
                    {
                        var img = MultimodalContentMapper.FromOpenAiImageUrl(url);
                        if (img != null) contents.Add(img);
                    }
                    break;
                }

                case "image_url": // legacy
                    if (part.TryGetProperty("image_url", out var iu2)
                        && iu2.TryGetProperty("url", out var u)
                        && u.GetString() is { Length: > 0 } url2)
                    {
                        var img = MultimodalContentMapper.FromOpenAiImageUrl(url2);
                        if (img != null) contents.Add(img);
                    }
                    break;

                case "input_file":
                {
                    string? fileId = part.TryGetProperty("file_id", out var fid) ? fid.GetString() : null;
                    string? fileUrl = part.TryGetProperty("file_url", out var fu) ? fu.GetString() : null;
                    string? fileData = part.TryGetProperty("file_data", out var fd) ? fd.GetString() : null;
                    string? filename = part.TryGetProperty("filename", out var fn) ? fn.GetString() : null;

                    if (!string.IsNullOrEmpty(fileData))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(fileData);
                            var dc = new DataContent(bytes, "application/pdf");
                            if (!string.IsNullOrEmpty(filename))
                            {
                                dc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                                dc.AdditionalProperties["transit.openai.filename"] = filename;
                            }
                            contents.Add(dc);
                        }
                        catch { /* malformed — drop */ }
                    }
                    else if (!string.IsNullOrEmpty(fileUrl))
                    {
                        contents.Add(new UriContent(fileUrl, "application/pdf"));
                    }
                    else if (!string.IsNullOrEmpty(fileId))
                    {
                        var uc = new UriContent($"openai-file://{fileId}", "application/pdf");
                        uc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                        uc.AdditionalProperties["transit.openai.file_id"] = fileId;
                        contents.Add(uc);
                    }
                    break;
                }

                case "reasoning":
                    if (part.TryGetProperty("text", out var rt) && rt.GetString() is { Length: > 0 } reasoning)
                        contents.Add(ThinkingMapper.CreateThinkingContent(reasoning));
                    break;

                case "input_audio":
                {
                    // Newer Responses API supports inline audio inputs (mirrors the
                    // Chat Completions input_audio shape). Decode to DataContent so
                    // routes to Chat / Gemini can preserve it.
                    if (part.TryGetProperty("input_audio", out var au)
                        && au.TryGetProperty("data", out var au64)
                        && au64.GetString() is { Length: > 0 } b64)
                    {
                        var fmt = au.TryGetProperty("format", out var f) ? f.GetString() ?? "mp3" : "mp3";
                        try
                        {
                            var bytes = Convert.FromBase64String(b64);
                            contents.Add(new DataContent(bytes, $"audio/{fmt}"));
                        }
                        catch { /* malformed audio — drop */ }
                    }
                    break;
                }
            }
        }

        return contents;
    }

    // ── Options ───────────────────────────────────────────────────────────────

    private static void ApplyScalars(JsonElement root, ChatOptions options)
    {
        if (root.TryGetProperty("temperature", out var temp) && temp.TryGetDouble(out var t))
            options.Temperature = SamplingScaleMapper.ClampTemperatureForOpenAiScale((float)t);
        if (root.TryGetProperty("top_p", out var topP) && topP.TryGetDouble(out var tp))
            options.TopP = SamplingScaleMapper.ClampTopP((float)tp);
        if (root.TryGetProperty("max_output_tokens", out var mo) && mo.TryGetInt32(out var moi))
            options.MaxOutputTokens = moi;
        if (root.TryGetProperty("frequency_penalty", out var fp) && fp.TryGetDouble(out var fpv))
            options.FrequencyPenalty = (float)Math.Clamp(fpv, -2.0, 2.0);
        if (root.TryGetProperty("presence_penalty", out var pp) && pp.TryGetDouble(out var ppv))
            options.PresencePenalty = (float)Math.Clamp(ppv, -2.0, 2.0);
    }

    private static void ApplyToolChoice(JsonElement root, ChatOptions options,
        Dictionary<string, object?> hints)
    {
        if (!root.TryGetProperty("tool_choice", out var tc)) return;
        hints[OpenAiHints.ToolChoice] = tc.Clone();

        if (tc.ValueKind == JsonValueKind.String)
        {
            options.ToolMode = tc.GetString() switch
            {
                "auto" => ChatToolMode.Auto,
                "required" => ChatToolMode.RequireAny,
                "none" => ChatToolMode.None,
                _ => options.ToolMode
            };
        }
        else if (tc.ValueKind == JsonValueKind.Object
                 && tc.TryGetProperty("type", out var ttype))
        {
            var tts = ttype.GetString();
            if (tts == "function"
                && tc.TryGetProperty("name", out var nm)
                && nm.GetString() is { Length: > 0 } fname)
            {
                options.ToolMode = ChatToolMode.RequireSpecific(fname);
            }
        }
    }

    private static IList<TransitFunctionToolDef>? DecodeTools(JsonElement root,
        Dictionary<string, object?> hints)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return null;

        var fnList = new List<TransitFunctionToolDef>();
        var builtins = new List<JsonElement>();

        foreach (var tool in tools.EnumerateArray())
        {
            var toolType = tool.TryGetProperty("type", out var tt) ? tt.GetString() : null;
            if (toolType == "function")
            {
                var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) continue;
                var desc = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
                var schema = tool.TryGetProperty("parameters", out var p) ? (JsonElement?)p.Clone() : null;
                fnList.Add(new TransitFunctionToolDef
                {
                    Name = name,
                    Description = desc,
                    ParametersSchema = schema
                });
            }
            else
            {
                // file_search / web_search / web_search_preview / computer_use_preview /
                // code_interpreter / image_generation / mcp / shell / apply_patch / custom — preserve raw
                builtins.Add(tool.Clone());
            }
        }

        if (builtins.Count > 0)
            hints[OpenAiHints.ResponsesBuiltinTools] = builtins;

        return fnList.Count > 0 ? fnList : null;
    }

    private static void BuildHints(JsonElement root, Dictionary<string, object?> hints)
    {
        if (root.TryGetProperty("previous_response_id", out var prev) && prev.GetString() is { } prevv)
            hints[OpenAiHints.PreviousResponseId] = prevv;

        if (root.TryGetProperty("store", out var st) && st.ValueKind != JsonValueKind.Null)
            hints[OpenAiHints.ResponsesStore] = st.ValueKind == JsonValueKind.True;

        if (root.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.Array)
            hints[OpenAiHints.ResponsesInclude] = inc.Clone();

        if (root.TryGetProperty("truncation", out var tr) && tr.GetString() is { } trv)
            hints[OpenAiHints.ResponsesTruncation] = trv;

        if (root.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
        {
            hints[OpenAiHints.Reasoning] = reasoning.Clone();
            // Also expose the scalar effort so cross-protocol encoders can fold
            // it into Anthropic thinking.budget_tokens / Gemini thinkingBudget.
            if (reasoning.TryGetProperty("effort", out var eff) && eff.GetString() is { } effv)
                hints[OpenAiHints.ReasoningEffort] = effv;
        }

        // Responses API uses `text.format` not `response_format`; keep both
        // hints separately so the encoder can emit the right one.
        if (root.TryGetProperty("text", out var textCfg) && textCfg.ValueKind == JsonValueKind.Object)
            hints["openai.responses.text"] = textCfg.Clone();

        if (root.TryGetProperty("parallel_tool_calls", out var ptc) && ptc.ValueKind != JsonValueKind.Null)
            hints[OpenAiHints.ParallelToolCalls] = ptc.ValueKind != JsonValueKind.False;

        if (root.TryGetProperty("service_tier", out var stier) && stier.GetString() is { } stierv)
            hints[OpenAiHints.ServiceTier] = stierv;

        if (root.TryGetProperty("user", out var u) && u.GetString() is { } uv)
            hints[OpenAiHints.User] = uv;

        if (root.TryGetProperty("safety_identifier", out var si) && si.GetString() is { } siv)
            hints[OpenAiHints.SafetyIdentifier] = siv;

        if (root.TryGetProperty("prompt_cache_key", out var pck) && pck.GetString() is { } pckv)
            hints[OpenAiHints.PromptCacheKey] = pckv;

        if (root.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
            hints["openai.responses.metadata"] = md.Clone();
    }

    private static IDictionary<string, object?>? ParseArguments(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }
        catch { return null; }
    }
}
