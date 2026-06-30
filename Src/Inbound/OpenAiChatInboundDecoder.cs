using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Inbound;

/// <summary>
/// Decodes OpenAI Chat Completions (<c>POST /v1/chat/completions</c>) JSON into
/// a <see cref="TransitRequest"/>.
/// <para>Captures <c>reasoning_content</c> on assistant messages
/// (DeepSeek / Kimi / Doubao / o1-style providers), <c>tool_choice</c> as both raw
/// hint and canonical <see cref="ChatToolMode"/>, plus all officially-supported
/// sampling and metadata fields.</para>
/// </summary>
public sealed class OpenAiChatInboundDecoder : IRequestDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.OpenAiChat;

    public TransitRequest Decode(byte[] requestBytes, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(requestBytes);
        var root = doc.RootElement;

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        var stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;

        var messages = DecodeMessages(root);
        var options = new ChatOptions();
        var hints = new Dictionary<string, object?>(StringComparer.Ordinal);
        var tools = DecodeTools(root);

        ApplyScalars(root, options);
        ApplyToolChoice(root, options, hints);
        BuildHints(root, hints);

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

    // ── Message decoding ──────────────────────────────────────────────────────

    private static IList<ChatMessage> DecodeMessages(JsonElement root)
    {
        var result = new List<ChatMessage>();
        if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var m in msgs.EnumerateArray())
        {
            var roleStr = m.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            var role = roleStr switch
            {
                "system" or "developer" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                "tool" or "function" => ChatRole.Tool,
                _ => ChatRole.User
            };

            var contents = DecodeContent(m, role);
            result.Add(new ChatMessage(role, contents));
        }

        return result;
    }

    private static List<AIContent> DecodeContent(JsonElement msgEl, ChatRole role)
    {
        var contents = new List<AIContent>();

        // Tool result messages (role=tool / function)
        if (role == ChatRole.Tool)
        {
            var toolCallId = msgEl.TryGetProperty("tool_call_id", out var tcid)
                ? tcid.GetString()
                : msgEl.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            object? result = null;
            if (msgEl.TryGetProperty("content", out var tcContent))
            {
                result = tcContent.ValueKind switch
                {
                    JsonValueKind.String => tcContent.GetString() ?? "",
                    JsonValueKind.Array or JsonValueKind.Object => (object)tcContent.Clone(),
                    _ => tcContent.ToString()
                };
            }
            contents.Add(new FunctionResultContent(toolCallId ?? "", result));
            return contents;
        }

        // Assistant messages — may contain text + reasoning_content + tool_calls
        if (role == ChatRole.Assistant)
        {
            // reasoning_content (DeepSeek / Kimi / Doubao / o1) carries the thinking
            // TEXT; the sibling reasoning object carries the opaque signature as
            // encrypted_content. Capture BOTH into a single thinking block so the
            // signature survives even when reasoning text is also present — needed
            // for the cross-protocol round-trip (Gemini thoughtSignature / Anthropic
            // signature tunnelled through PA). Reading them independently (the old
            // if/else) dropped the signature whenever reasoning_content was present.
            var reasoningText = msgEl.TryGetProperty("reasoning_content", out var rc)
                ? rc.GetString() : null;
            var reasoningEnc = msgEl.TryGetProperty("reasoning", out var reasoningObj)
                               && reasoningObj.ValueKind == JsonValueKind.Object
                               && reasoningObj.TryGetProperty("encrypted_content", out var ec)
                ? ec.GetString() : null;
            if (!string.IsNullOrEmpty(reasoningText) || !string.IsNullOrEmpty(reasoningEnc))
            {
                contents.Add(ThinkingMapper.CreateThinkingContent(
                    reasoningText ?? "", openAiEncryptedContent: reasoningEnc));
            }

            // Text content alongside tool_calls
            if (msgEl.TryGetProperty("content", out var ace))
            {
                AppendUserOrAssistantText(ace, contents);
            }

            // Tool calls
            if (msgEl.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var tcId = tc.TryGetProperty("id", out var tcIdEl) ? tcIdEl.GetString() ?? "" : "";
                    var fnName = "";
                    var fnArgs = "";
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        fnName = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        fnArgs = fn.TryGetProperty("arguments", out var a)
                            ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.GetRawText()
                            : "";
                    }
                    contents.Add(new FunctionCallContent(
                        tcId, fnName, fnArgs.Length > 0 ? ParseArguments(fnArgs) : null));
                }
            }
            return contents;
        }

        // User / System messages
        if (msgEl.TryGetProperty("content", out var contentEl))
            AppendUserOrAssistantText(contentEl, contents);

        return contents;
    }

    private static void AppendUserOrAssistantText(JsonElement contentEl, List<AIContent> contents)
    {
        switch (contentEl.ValueKind)
        {
            case JsonValueKind.String:
                if (contentEl.GetString() is { Length: > 0 } txt)
                    contents.Add(new TextContent(txt));
                break;

            case JsonValueKind.Array:
                foreach (var part in contentEl.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() ?? "" : "";
                    switch (partType)
                    {
                        case "text":
                            if (part.TryGetProperty("text", out var ptxt)
                                && ptxt.GetString() is { Length: > 0 } t)
                                contents.Add(new TextContent(t));
                            break;

                        case "image_url":
                            if (part.TryGetProperty("image_url", out var imgUrl)
                                && imgUrl.TryGetProperty("url", out var urlEl)
                                && urlEl.GetString() is { Length: > 0 } url)
                            {
                                var aiContent = MultimodalContentMapper.FromOpenAiImageUrl(url);
                                if (aiContent != null) contents.Add(aiContent);
                            }
                            break;

                        case "input_audio":
                            // Audio inputs — preserved as DataContent
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

                        case "file":
                            // OpenAI file uploads (PDFs etc.)
                            if (part.TryGetProperty("file", out var file))
                            {
                                if (file.TryGetProperty("file_data", out var fd)
                                    && fd.GetString() is { Length: > 0 } fdv)
                                {
                                    try
                                    {
                                        var bytes = Convert.FromBase64String(fdv);
                                        var name = file.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                                        var dc = new DataContent(bytes, "application/pdf");
                                        if (!string.IsNullOrEmpty(name))
                                        {
                                            dc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                                            dc.AdditionalProperties["transit.openai.filename"] = name;
                                        }
                                        contents.Add(dc);
                                    }
                                    catch { /* malformed file — drop */ }
                                }
                                else if (file.TryGetProperty("file_id", out var fid)
                                         && fid.GetString() is { Length: > 0 } fidv)
                                {
                                    var uc = new UriContent($"openai-file://{fidv}", "application/pdf");
                                    uc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                                    uc.AdditionalProperties["transit.openai.file_id"] = fidv;
                                    contents.Add(uc);
                                }
                            }
                            break;
                    }
                }
                break;
        }
    }

    // ── Options decoding ──────────────────────────────────────────────────────

    private static void ApplyScalars(JsonElement root, ChatOptions options)
    {
        // OpenAI Chat already uses the IR [0, 2] scale; clamp defensively.
        if (root.TryGetProperty("temperature", out var temp) && temp.TryGetDouble(out var t))
            options.Temperature = SamplingScaleMapper.ClampTemperatureForOpenAiScale((float)t);
        if (root.TryGetProperty("top_p", out var topP) && topP.TryGetDouble(out var tp))
            options.TopP = SamplingScaleMapper.ClampTopP((float)tp);
        if (root.TryGetProperty("max_completion_tokens", out var mct) && mct.TryGetInt32(out var mc))
            options.MaxOutputTokens = mc;
        else if (root.TryGetProperty("max_tokens", out var mt) && mt.TryGetInt32(out var mti))
            options.MaxOutputTokens = mti;

        if (root.TryGetProperty("frequency_penalty", out var fp) && fp.TryGetDouble(out var fpv))
            options.FrequencyPenalty = (float)Math.Clamp(fpv, -2.0, 2.0);
        if (root.TryGetProperty("presence_penalty", out var pp) && pp.TryGetDouble(out var ppv))
            options.PresencePenalty = (float)Math.Clamp(ppv, -2.0, 2.0);
        if (root.TryGetProperty("seed", out var seed) && seed.TryGetInt64(out var seedVal))
            options.Seed = seedVal;

        // Stop sequences (string | array)
        if (root.TryGetProperty("stop", out var stop))
        {
            if (stop.ValueKind == JsonValueKind.String && stop.GetString() is { } sv)
                options.StopSequences = [sv];
            else if (stop.ValueKind == JsonValueKind.Array)
                options.StopSequences = stop.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .ToList();
        }
    }

    private static void ApplyToolChoice(JsonElement root, ChatOptions options,
        Dictionary<string, object?> hints)
    {
        if (!root.TryGetProperty("tool_choice", out var tc)) return;

        // Raw passthrough for OpenAI → OpenAI
        hints[OpenAiHints.ToolChoice] = tc.Clone();

        // Project into the canonical ChatToolMode for cross-protocol routing
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
                 && tc.TryGetProperty("type", out var ttype)
                 && ttype.GetString() == "function"
                 && tc.TryGetProperty("function", out var fn)
                 && fn.TryGetProperty("name", out var name)
                 && name.GetString() is { Length: > 0 } fname)
        {
            options.ToolMode = ChatToolMode.RequireSpecific(fname);
        }
    }

    private static IList<TransitFunctionToolDef>? DecodeTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return null;

        var toolList = new List<TransitFunctionToolDef>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (!tool.TryGetProperty("function", out var fn)) continue;
            var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            var desc = fn.TryGetProperty("description", out var d) ? d.GetString() : null;
            var schema = fn.TryGetProperty("parameters", out var p) ? (JsonElement?)p.Clone() : null;
            toolList.Add(new TransitFunctionToolDef { Name = name, Description = desc, ParametersSchema = schema });
        }
        return toolList.Count > 0 ? toolList : null;
    }

    // ── Hints ─────────────────────────────────────────────────────────────────

    private static void BuildHints(JsonElement root, Dictionary<string, object?> hints)
    {
        if (root.TryGetProperty("response_format", out var rf) && rf.ValueKind == JsonValueKind.Object)
            hints[OpenAiHints.ResponseFormat] = rf.Clone();

        if (root.TryGetProperty("parallel_tool_calls", out var ptc) && ptc.ValueKind != JsonValueKind.Null)
            hints[OpenAiHints.ParallelToolCalls] = ptc.ValueKind != JsonValueKind.False;

        if (root.TryGetProperty("service_tier", out var st) && st.GetString() is { } stv)
            hints[OpenAiHints.ServiceTier] = stv;

        if (root.TryGetProperty("logit_bias", out var lb) && lb.ValueKind == JsonValueKind.Object)
            hints[OpenAiHints.LogitBias] = lb.Clone();

        if (root.TryGetProperty("logprobs", out var lp) && lp.ValueKind == JsonValueKind.True)
            hints[OpenAiHints.Logprobs] = true;

        if (root.TryGetProperty("top_logprobs", out var tlp) && tlp.TryGetInt32(out var tlpv))
            hints[OpenAiHints.TopLogprobs] = tlpv;

        if (root.TryGetProperty("n", out var nEl) && nEl.TryGetInt32(out var nv))
            hints[OpenAiHints.CandidateCount] = nv;

        if (root.TryGetProperty("user", out var user) && user.GetString() is { } uv)
            hints[OpenAiHints.User] = uv;

        if (root.TryGetProperty("prompt_cache_key", out var pck) && pck.GetString() is { } pckv)
            hints[OpenAiHints.PromptCacheKey] = pckv;

        if (root.TryGetProperty("safety_identifier", out var si) && si.GetString() is { } siv)
            hints[OpenAiHints.SafetyIdentifier] = siv;

        // reasoning_effort — top-level on Chat Completions for the o-series/gpt-5
        // (https://platform.openai.com/docs/api-reference/chat/create#chat-create-reasoning_effort).
        // Kept here as a string hint so cross-protocol routes can fold it into
        // Anthropic thinking.budget_tokens / Gemini thinkingConfig.thinkingLevel.
        if (root.TryGetProperty("reasoning_effort", out var re) && re.GetString() is { } rev)
            hints[OpenAiHints.ReasoningEffort] = rev;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IDictionary<string, object?>? ParseArguments(string json)
    {
        try
        {
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
