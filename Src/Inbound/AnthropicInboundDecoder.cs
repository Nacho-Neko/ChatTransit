using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Inbound;

/// <summary>
/// Decodes Anthropic Messages API (<c>POST /v1/messages</c>) JSON into a <see cref="TransitRequest"/>.
/// <para>Preserves every officially documented field — including the cryptographic
/// blobs (<c>thinking.signature</c>, <c>redacted_thinking.data</c>) that the API
/// rejects multi-turn requests for if modified, plus <c>cache_control</c>,
/// <c>tool_choice</c>, <c>metadata</c>, <c>document</c> blocks, and the
/// <c>tool_result.content</c> sub-block array (text + image).</para>
/// </summary>
public sealed class AnthropicInboundDecoder : IRequestDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Anthropic;

    public TransitRequest Decode(byte[] requestBytes, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(requestBytes);
        var root = doc.RootElement;

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        var stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;

        var hints = new Dictionary<string, object?>(StringComparer.Ordinal);

        var systemText = ParseSystem(root, hints);
        var messages = DecodeMessages(root, systemText);
        var options = new ChatOptions();
        ApplyScalars(root, options);
        ApplyToolChoice(root, options, hints);
        BuildRequestHints(root, hints);
        var tools = DecodeTools(root);

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

    // ── System prompt ─────────────────────────────────────────────────────────

    private static string? ParseSystem(JsonElement root, Dictionary<string, object?> hints)
    {
        if (!root.TryGetProperty("system", out var systemEl)) return null;

        switch (systemEl.ValueKind)
        {
            case JsonValueKind.String:
                return systemEl.GetString();

            case JsonValueKind.Array:
                hints[AnthropicHints.SystemBlocks] = systemEl.Clone();
                var parts = new List<string>();
                foreach (var b in systemEl.EnumerateArray())
                {
                    if (b.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                        && b.TryGetProperty("text", out var btxt)
                        && btxt.GetString() is { Length: > 0 } txt)
                        parts.Add(txt);
                }
                return parts.Count > 0 ? string.Join("\n\n", parts) : null;
        }

        return null;
    }

    // ── Message decoding ──────────────────────────────────────────────────────

    private static IList<ChatMessage> DecodeMessages(JsonElement root, string? systemText)
    {
        var result = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemText))
            result.Add(new ChatMessage(ChatRole.System, systemText));

        if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var msgEl in msgs.EnumerateArray())
        {
            var roleStr = msgEl.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            var role = roleStr == "assistant" ? ChatRole.Assistant : ChatRole.User;

            var contents = DecodeContent(msgEl);
            result.Add(new ChatMessage(role, contents));
        }

        return result;
    }

    private static List<AIContent> DecodeContent(JsonElement msgEl)
    {
        var contents = new List<AIContent>();
        if (!msgEl.TryGetProperty("content", out var contentEl)) return contents;

        if (contentEl.ValueKind == JsonValueKind.String)
        {
            var text = contentEl.GetString() ?? "";
            if (text.Length > 0) contents.Add(new TextContent(text));
            return contents;
        }

        if (contentEl.ValueKind != JsonValueKind.Array) return contents;

        foreach (var block in contentEl.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() ?? "text" : "text";
            switch (blockType)
            {
                case "text":
                    if (block.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } t)
                    {
                        var tc = new TextContent(t);
                        AttachCacheControl(tc, block);
                        contents.Add(tc);
                    }
                    break;

                case "thinking":
                {
                    var thinkingText = block.TryGetProperty("thinking", out var th)
                        ? th.GetString() ?? "" : "";
                    var signature = block.TryGetProperty("signature", out var sigEl)
                        ? sigEl.GetString() : null;
                    var thinkingContent = ThinkingMapper.CreateThinkingContent(
                        thinkingText, anthropicSignature: signature);
                    AttachCacheControl(thinkingContent, block);
                    contents.Add(thinkingContent);
                    break;
                }

                case "redacted_thinking":
                {
                    var data = block.TryGetProperty("data", out var dataEl)
                        ? dataEl.GetString() ?? "" : "";
                    var redacted = ThinkingMapper.CreateRedactedThinkingContent(data);
                    AttachCacheControl(redacted, block);
                    contents.Add(redacted);
                    break;
                }

                case "tool_use":
                {
                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    IDictionary<string, object?>? input = null;
                    if (block.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Object)
                    {
                        input = new Dictionary<string, object?>();
                        foreach (var prop in inputEl.EnumerateObject())
                            ((Dictionary<string, object?>)input)[prop.Name] = prop.Value.Clone();
                    }
                    var fcc = new FunctionCallContent(id, name, input);
                    AttachCacheControl(fcc, block);
                    contents.Add(fcc);
                    break;
                }

                case "tool_result":
                {
                    var toolUseId = block.TryGetProperty("tool_use_id", out var tuId)
                        ? tuId.GetString() ?? "" : "";
                    object? result = null;
                    if (block.TryGetProperty("content", out var tc))
                    {
                        // tool_result.content can be either a string OR an array of
                        // content blocks (text + image). We preserve the structured
                        // form as a JsonElement so the outbound encoder can replay it.
                        result = tc.ValueKind switch
                        {
                            JsonValueKind.String => tc.GetString() ?? "",
                            JsonValueKind.Array or JsonValueKind.Object => (object)tc.Clone(),
                            _ => tc.ToString()
                        };
                    }
                    var frc = new FunctionResultContent(toolUseId, result);
                    if (block.TryGetProperty("is_error", out var isErr)
                        && isErr.ValueKind == JsonValueKind.True)
                    {
                        frc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                        frc.AdditionalProperties["transit.tool_result.is_error"] = true;
                    }
                    AttachCacheControl(frc, block);
                    contents.Add(frc);
                    break;
                }

                case "image":
                    AddImageBlock(block, contents);
                    break;

                case "document":
                    AddDocumentBlock(block, contents);
                    break;

                default:
                    // Unknown block types (server_tool_use, mcp_tool_use, etc.) are
                    // preserved as an opaque TextContent carrying the raw JSON so
                    // same-protocol round-trip stays lossless. Cross-protocol they
                    // degrade to text — there's no analogue elsewhere.
                    var raw = block.GetRawText();
                    var opaque = new TextContent($"[{blockType}]");
                    opaque.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    opaque.AdditionalProperties["transit.anthropic.raw_block"] = raw;
                    opaque.AdditionalProperties["transit.anthropic.raw_block_type"] = blockType;
                    AttachCacheControl(opaque, block);
                    contents.Add(opaque);
                    break;
            }
        }

        return contents;
    }

    private static void AddImageBlock(JsonElement block, List<AIContent> contents)
    {
        if (!block.TryGetProperty("source", out var src)) return;

        var srcType = src.TryGetProperty("type", out var st) ? st.GetString() ?? "" : "";
        AIContent? img = null;

        if (srcType == "base64"
            && src.TryGetProperty("media_type", out var mt)
            && src.TryGetProperty("data", out var d))
        {
            img = MultimodalContentMapper.FromAnthropicBase64Source(
                mt.GetString() ?? "image/png", d.GetString() ?? "");
        }
        else if (srcType == "url" && src.TryGetProperty("url", out var urlEl))
        {
            img = MultimodalContentMapper.FromAnthropicUrlSource(urlEl.GetString() ?? "");
        }
        // "file" (file_id reference) — Anthropic Files API
        else if (srcType == "file" && src.TryGetProperty("file_id", out var fileIdEl))
        {
            var u = new UriContent($"file://anthropic/{fileIdEl.GetString() ?? ""}", "image/*");
            u.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            u.AdditionalProperties["transit.anthropic.file_id"] = fileIdEl.GetString() ?? "";
            img = u;
        }

        if (img != null)
        {
            AttachCacheControl(img, block);
            contents.Add(img);
        }
    }

    private static void AddDocumentBlock(JsonElement block, List<AIContent> contents)
    {
        if (!block.TryGetProperty("source", out var src)) return;

        var srcType = src.TryGetProperty("type", out var st) ? st.GetString() ?? "" : "";
        AIContent? doc = null;

        if (srcType == "base64"
            && src.TryGetProperty("media_type", out var mt)
            && src.TryGetProperty("data", out var d))
        {
            var mediaType = mt.GetString() ?? "application/pdf";
            doc = MultimodalContentMapper.FromAnthropicBase64Source(mediaType, d.GetString() ?? "");
        }
        else if (srcType == "url" && src.TryGetProperty("url", out var urlEl))
        {
            var u = urlEl.GetString() ?? "";
            doc = string.IsNullOrEmpty(u) ? null : new UriContent(u, "application/pdf");
        }
        else if (srcType == "text" && src.TryGetProperty("data", out var textData))
        {
            // Plain-text document — passes through as TextContent
            var raw = textData.GetString() ?? "";
            if (raw.Length > 0) doc = new TextContent(raw);
        }
        else if (srcType == "content" && src.TryGetProperty("content", out var contentArr))
        {
            // Already-decoded content blocks — flatten as text
            var sb = new System.Text.StringBuilder();
            if (contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in contentArr.EnumerateArray())
                {
                    if (b.TryGetProperty("text", out var btxt))
                        sb.AppendLine(btxt.GetString());
                }
            }
            if (sb.Length > 0) doc = new TextContent(sb.ToString());
        }

        if (doc != null)
        {
            // Preserve the document's title / context / citations metadata for round-trip
            doc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            doc.AdditionalProperties["transit.anthropic.document"] = true;
            if (block.TryGetProperty("title", out var title))
                doc.AdditionalProperties["transit.anthropic.document.title"] = title.GetString();
            if (block.TryGetProperty("context", out var ctxEl))
                doc.AdditionalProperties["transit.anthropic.document.context"] = ctxEl.GetString();
            if (block.TryGetProperty("citations", out var citEl))
                doc.AdditionalProperties["transit.anthropic.document.citations"] = citEl.Clone();
            AttachCacheControl(doc, block);
            contents.Add(doc);
        }
    }

    private static void AttachCacheControl(AIContent target, JsonElement block)
    {
        if (!block.TryGetProperty("cache_control", out var cc)
            || cc.ValueKind == JsonValueKind.Null
            || cc.ValueKind == JsonValueKind.Undefined) return;
        target.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        target.AdditionalProperties[AnthropicHints.CacheControl] = cc.Clone();
    }

    // ── Options ───────────────────────────────────────────────────────────────

    private static void ApplyScalars(JsonElement root, ChatOptions options)
    {
        if (root.TryGetProperty("max_tokens", out var mt) && mt.TryGetInt32(out var mti))
            options.MaxOutputTokens = mti;
        // Anthropic's wire scale is [0, 1] but the IR convention is [0, 2]
        // (OpenAI/Gemini-aligned). Rescale ×2 so cross-protocol traffic to
        // OpenAI/Gemini gets the semantically equivalent value.
        if (root.TryGetProperty("temperature", out var temp) && temp.TryGetDouble(out var t))
            options.Temperature = SamplingScaleMapper.NormalizeTemperatureFromAnthropic((float)t);
        if (root.TryGetProperty("top_p", out var tp) && tp.TryGetDouble(out var tpv))
            options.TopP = SamplingScaleMapper.ClampTopP((float)tpv);
        if (root.TryGetProperty("top_k", out var tk) && tk.TryGetInt32(out var tkv))
            options.TopK = SamplingScaleMapper.ClampTopK(tkv);
        if (root.TryGetProperty("stop_sequences", out var ss) && ss.ValueKind == JsonValueKind.Array)
        {
            options.StopSequences = ss.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList();
        }
    }

    private static void ApplyToolChoice(JsonElement root, ChatOptions options,
        Dictionary<string, object?> hints)
    {
        if (!root.TryGetProperty("tool_choice", out var tc) || tc.ValueKind != JsonValueKind.Object)
            return;

        // Preserve the raw object for Anthropic→Anthropic passthrough
        hints[AnthropicHints.ToolChoice] = tc.Clone();

        // Project into the IR canonical ChatToolMode for cross-protocol routing
        var type = tc.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        options.ToolMode = type switch
        {
            "auto" => ChatToolMode.Auto,
            "any" => ChatToolMode.RequireAny,
            "tool" when tc.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { } n
                => ChatToolMode.RequireSpecific(n),
            "none" => ChatToolMode.None,
            _ => options.ToolMode
        };

        // Anthropic's `tool_choice.disable_parallel_tool_use:true` is the wire
        // equivalent of OpenAI's top-level `parallel_tool_calls:false`. Project
        // it onto the OpenAI hint so cross-protocol routes carry the toggle.
        if (tc.TryGetProperty("disable_parallel_tool_use", out var dp)
            && dp.ValueKind != JsonValueKind.Null)
        {
            // disable_parallel_tool_use=true ⇒ parallel_tool_calls=false
            hints[OpenAiHints.ParallelToolCalls] = dp.ValueKind != JsonValueKind.True;
        }
    }

    private static IList<TransitFunctionToolDef>? DecodeTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return null;

        var toolList = new List<TransitFunctionToolDef>();
        foreach (var tool in tools.EnumerateArray())
        {
            // Skip built-in / server-side tool wrappers (web_search_20250305, etc.)
            // when they don't have an input_schema — they're caller-side concerns.
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            var desc = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
            var schema = tool.TryGetProperty("input_schema", out var p) ? (JsonElement?)p.Clone() : null;
            toolList.Add(new TransitFunctionToolDef { Name = name, Description = desc, ParametersSchema = schema });
        }
        return toolList.Count > 0 ? toolList : null;
    }

    private static void BuildRequestHints(JsonElement root, Dictionary<string, object?> hints)
    {
        if (root.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
            hints[AnthropicHints.Metadata] = md.Clone();

        if (root.TryGetProperty("container", out var c) && c.GetString() is { } cVal)
            hints[AnthropicHints.Container] = cVal;

        if (root.TryGetProperty("service_tier", out var st) && st.GetString() is { } stVal)
            hints[AnthropicHints.ServiceTier] = stVal;

        if (root.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.Object)
        {
            hints[AnthropicHints.ThinkingConfig] = thinking.Clone();
            var thType = thinking.TryGetProperty("type", out var thType2) ? thType2.GetString() : null;
            if (thType is "enabled" or "adaptive")
                hints[AnthropicHints.IsThinkingModel] = true;
        }
    }
}
