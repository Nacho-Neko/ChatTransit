using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Outbound;

/// <summary>
/// Encodes a <see cref="TransitRequest"/> into Anthropic Messages API JSON bytes.
/// Faithfully rebuilds thinking / redacted_thinking blocks with their cryptographic
/// signature / data fields, replays per-block cache_control, supports document
/// blocks, structured tool_result content arrays, and metadata.
/// </summary>
public sealed class AnthropicOutboundEncoder : IRequestEncoder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public ChatTransitProtocol Protocol => ChatTransitProtocol.Anthropic;

    public byte[] Encode(TransitRequest request)
    {
        var body = new Dictionary<string, object?>();

        body["model"] = request.Model;
        body["stream"] = request.Stream;

        // ── System prompt ─────────────────────────────────────────────────────
        // Prefer the original system blocks (with cache_control etc.) over the
        // flattened text whenever we have them.
        if (request.Hints.TryGetValue(AnthropicHints.SystemBlocks, out var sysBlocks)
            && sysBlocks is JsonElement sysBlocksEl
            && sysBlocksEl.ValueKind == JsonValueKind.Array)
        {
            body["system"] = sysBlocksEl;
        }
        else
        {
            var systemParts = request.Messages
                .Where(m => m.Role == ChatRole.System)
                .SelectMany(m => m.Contents.OfType<TextContent>())
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (systemParts.Count > 0)
                body["system"] = string.Join("\n\n", systemParts);
        }

        var nonSystem = request.Messages.Where(m => m.Role != ChatRole.System).ToList();
        body["messages"] = BuildMessages(nonSystem);

        // ── Sampling ──────────────────────────────────────────────────────────
        // Anthropic wire scale is [0, 1] while the IR is [0, 2]; SamplingScaleMapper
        // ÷2 here mirrors the ×2 in the decoder. Without it an OpenAI caller
        // sending temperature=1.5 would get a 422 from Anthropic.
        var opts = request.Options;
        body["max_tokens"] = opts.MaxOutputTokens ?? 4096;
        if (opts.Temperature.HasValue)
            body["temperature"] = SamplingScaleMapper.DenormalizeTemperatureToAnthropic(opts.Temperature.Value);
        if (opts.TopP.HasValue) body["top_p"] = SamplingScaleMapper.ClampTopP(opts.TopP.Value);
        if (opts.TopK.HasValue) body["top_k"] = SamplingScaleMapper.ClampTopK(opts.TopK.Value);
        if (opts.StopSequences is { Count: > 0 }) body["stop_sequences"] = opts.StopSequences;

        // ── Thinking config ───────────────────────────────────────────────────
        // Extended thinking forbids temperature/top_p/top_k overrides; strip
        // and inject thinking.budget_tokens. Cross-protocol: OpenAI's
        // `reasoning_effort` (or Gemini `thinkingLevel`) implicitly turns
        // thinking on with a corresponding budget.
        var effortBudget = MapReasoningEffortToBudget(request.Hints);
        var isThinking = (request.Hints.TryGetValue(AnthropicHints.IsThinkingModel, out var itv)
                          && itv is true)
                         || effortBudget.HasValue;
        if (isThinking)
        {
            body.Remove("temperature");
            body.Remove("top_p");
            body.Remove("top_k");

            object thinkingBlock;
            if (request.Hints.TryGetValue(AnthropicHints.ThinkingConfig, out var tcfg)
                && tcfg is JsonElement tcfgEl
                && tcfgEl.ValueKind == JsonValueKind.Object)
            {
                thinkingBlock = tcfgEl;
            }
            else if (effortBudget.HasValue)
            {
                var maxTok = (int)(opts.MaxOutputTokens ?? Math.Max(effortBudget.Value + 1024, 4096));
                if (maxTok <= effortBudget.Value) { maxTok = effortBudget.Value + 1024; body["max_tokens"] = maxTok; }
                thinkingBlock = new { type = "enabled", budget_tokens = (long)effortBudget.Value };
            }
            else
            {
                var maxTok = (int)(opts.MaxOutputTokens ?? 4096);
                if (maxTok < 16384) { maxTok = 16384; body["max_tokens"] = maxTok; }
                var budget = (long)Math.Clamp(maxTok * 3 / 4, 1024, maxTok - 1);
                thinkingBlock = new { type = "enabled", budget_tokens = budget };
            }
            body["thinking"] = thinkingBlock;
        }

        // ── Tools ─────────────────────────────────────────────────────────────
        if (request.FunctionTools is { Count: > 0 })
        {
            body["tools"] = request.FunctionTools.Select(t => (object)new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.ParametersSchema ?? JsonSerializer.SerializeToElement(
                    new { type = "object", properties = new { } })
            }).ToList();
        }

        // ── Tool choice ───────────────────────────────────────────────────────
        // Prefer the original Anthropic-shaped tool_choice from hints (Anthropic
        // → Anthropic passthrough); otherwise project from the IR ChatToolMode.
        // Cross-protocol: OpenAI's `parallel_tool_calls:false` ⇒ inject
        // `disable_parallel_tool_use:true` into the projected tool_choice.
        var disableParallel = request.Hints.TryGetValue(OpenAiHints.ParallelToolCalls, out var ptcVal)
                              && ptcVal is false;
        if (request.Hints.TryGetValue(AnthropicHints.ToolChoice, out var tc) && tc is JsonElement tcEl)
        {
            body["tool_choice"] = tcEl;
        }
        else if (opts.ToolMode is { } toolMode)
        {
            var projected = ProjectToolMode(toolMode, disableParallel);
            if (projected != null) body["tool_choice"] = projected;
        }
        else if (disableParallel)
        {
            // No tool mode but the caller explicitly asked for serial tool use —
            // default to {type:"auto", disable_parallel_tool_use:true}.
            body["tool_choice"] = new Dictionary<string, object?>
            {
                ["type"] = "auto",
                ["disable_parallel_tool_use"] = true
            };
        }

        // ── Hint passthrough (metadata / container / service_tier) ────────────
        if (request.Hints.TryGetValue(AnthropicHints.Metadata, out var md) && md is JsonElement mdEl)
            body["metadata"] = mdEl;
        if (request.Hints.TryGetValue(AnthropicHints.Container, out var c) && c is string cStr)
            body["container"] = cStr;
        if (request.Hints.TryGetValue(AnthropicHints.ServiceTier, out var st) && st is string stStr)
            body["service_tier"] = stStr;

        return JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Folds OpenAI's <c>reasoning_effort</c> / Gemini <c>thinkingLevel</c> /
    /// Gemini <c>thinkingBudget</c> into a concrete token budget for Anthropic's
    /// <c>thinking.budget_tokens</c>. Returns null when no caller-side reasoning
    /// hint is present. The mapping mirrors OpenAI's published guidance —
    /// <c>minimal</c> ≈ 1k, <c>low</c> ≈ 4k, <c>medium</c> ≈ 8k, <c>high</c> ≈ 16k.
    /// </summary>
    private static int? MapReasoningEffortToBudget(IReadOnlyDictionary<string, object?> hints)
    {
        if (hints.TryGetValue(Hints.GeminiHints.ThinkingBudget, out var tbv) && tbv is int tb && tb > 0)
            return tb;
        string? effort = null;
        if (hints.TryGetValue(OpenAiHints.ReasoningEffort, out var re) && re is string reStr)
            effort = reStr;
        else if (hints.TryGetValue(Hints.GeminiHints.ThinkingLevel, out var tl) && tl is string tlStr)
            effort = tlStr;
        return effort?.ToLowerInvariant() switch
        {
            "minimal" or "none" => 1024,
            "low" => 4096,
            "medium" => 8192,
            "high" => 16384,
            _ => null
        };
    }

    private static object? ProjectToolMode(ChatToolMode mode, bool disableParallel)
    {
        Dictionary<string, object?>? choice = mode switch
        {
            NoneChatToolMode => new() { ["type"] = "none" },
            AutoChatToolMode => new() { ["type"] = "auto" },
            RequiredChatToolMode r when !string.IsNullOrEmpty(r.RequiredFunctionName)
                => new() { ["type"] = "tool", ["name"] = r.RequiredFunctionName },
            RequiredChatToolMode => new() { ["type"] = "any" },
            _ => null
        };

        // `disable_parallel_tool_use` only makes sense on auto / any / tool —
        // skip it on `none` per the API spec.
        if (choice != null && disableParallel
            && choice["type"] as string is "auto" or "any" or "tool")
        {
            choice["disable_parallel_tool_use"] = true;
        }
        return choice;
    }

    private static List<object> BuildMessages(IList<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var msg in messages)
        {
            var role = msg.Role == ChatRole.Assistant ? "assistant" : "user";
            var blocks = BuildContent(msg.Contents);
            if (blocks.Count == 0) blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text", ["text"] = "."
            });

            // Use string shorthand only when there's a single plain text block
            // AND no cache_control / other metadata on it.
            if (blocks.Count == 1 && blocks[0] is Dictionary<string, object?> d
                && d.TryGetValue("type", out var bt) && bt?.ToString() == "text"
                && d.TryGetValue("text", out var btxt)
                && !d.ContainsKey("cache_control"))
            {
                result.Add(new { role, content = btxt?.ToString() ?? "" });
            }
            else
            {
                result.Add(new { role, content = blocks });
            }
        }
        return result;
    }

    private static List<object> BuildContent(IList<AIContent> contents)
    {
        var blocks = new List<object>();
        foreach (var c in contents)
        {
            object? block;
            if (ThinkingMapper.IsThinkingContent(c))
            {
                block = BuildThinkingBlock(c);
            }
            else
            {
                block = c switch
                {
                    TextContent tc when !string.IsNullOrEmpty(tc.Text) => BuildTextBlock(tc),
                    DataContent dc => BuildDataBlock(dc),
                    UriContent uc => BuildUriBlock(uc),
                    FunctionCallContent fcc => BuildToolUseBlock(fcc),
                    FunctionResultContent frc => BuildToolResultBlock(frc),
                    _ => null
                };
            }
            if (block != null) blocks.Add(block);
        }
        return blocks;
    }

    private static object BuildThinkingBlock(AIContent content)
    {
        if (ThinkingMapper.IsRedactedThinking(content))
        {
            var data = ThinkingMapper.GetAnthropicRedactedData(content) ?? "";
            return new Dictionary<string, object?>
            {
                ["type"] = "redacted_thinking",
                ["data"] = data
            };
        }

        var text = ThinkingMapper.GetThinkingText(content) ?? "";
        // Recover the signature from any protocol carrier: a cross-protocol caller
        // (OpenAI/Gemini client) routed onto an Anthropic-native backend replays the
        // blob under its own key, but Anthropic requires it as `signature` or it
        // 400s with "messages.N.content.0.thinking.signature: Field required".
        var sig = ThinkingMapper.GetAnySignature(content);
        var block = new Dictionary<string, object?>
        {
            ["type"] = "thinking",
            ["thinking"] = text
        };
        // Anthropic requires the original signature byte-for-byte on round-trip
        if (!string.IsNullOrEmpty(sig)) block["signature"] = sig;
        AttachCacheControl(block, content);
        return block;
    }

    private static object BuildTextBlock(TextContent tc)
    {
        // Restore opaque blocks (server_tool_use / mcp_tool_use / etc.) byte-for-byte
        if (tc.AdditionalProperties?.TryGetValue("transit.anthropic.raw_block", out var raw) == true
            && raw is string rawJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                return doc.RootElement.Clone();
            }
            catch { /* fall through to plain text */ }
        }

        var block = new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = tc.Text ?? ""
        };
        AttachCacheControl(block, tc);
        return block;
    }

    private static object? BuildDataBlock(DataContent dc)
    {
        // Document (PDF) round-trip
        if (dc.AdditionalProperties?.TryGetValue("transit.anthropic.document", out var isDoc) == true
            && isDoc is true)
        {
            var b64 = MultimodalContentMapper.ToAnthropicBase64(dc);
            if (b64 == null) return null;
            var block = new Dictionary<string, object?>
            {
                ["type"] = "document",
                ["source"] = new
                {
                    type = b64.Value.type,
                    media_type = b64.Value.mediaType,
                    data = b64.Value.data
                }
            };
            CopyDocumentMetadata(block, dc);
            AttachCacheControl(block, dc);
            return block;
        }

        var img = MultimodalContentMapper.ToAnthropicBase64(dc);
        if (img == null) return null;
        var imgBlock = new Dictionary<string, object?>
        {
            ["type"] = "image",
            ["source"] = new
            {
                type = img.Value.type,
                media_type = img.Value.mediaType,
                data = img.Value.data
            }
        };
        AttachCacheControl(imgBlock, dc);
        return imgBlock;
    }

    private static object? BuildUriBlock(UriContent uc)
    {
        // Document URL
        if (uc.AdditionalProperties?.TryGetValue("transit.anthropic.document", out var isDoc) == true
            && isDoc is true)
        {
            var block = new Dictionary<string, object?>
            {
                ["type"] = "document",
                ["source"] = new { type = "url", url = uc.Uri?.ToString() ?? "" }
            };
            CopyDocumentMetadata(block, uc);
            AttachCacheControl(block, uc);
            return block;
        }

        // Anthropic Files API reference
        if (uc.AdditionalProperties?.TryGetValue("transit.anthropic.file_id", out var fid) == true
            && fid is string fileId)
        {
            var b = new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["source"] = new { type = "file", file_id = fileId }
            };
            AttachCacheControl(b, uc);
            return b;
        }

        var url = MultimodalContentMapper.ToAnthropicUrl(uc);
        if (url == null) return null;
        var imgBlock = new Dictionary<string, object?>
        {
            ["type"] = "image",
            ["source"] = new { type = url.Value.type, url = url.Value.url }
        };
        AttachCacheControl(imgBlock, uc);
        return imgBlock;
    }

    private static object BuildToolUseBlock(FunctionCallContent fcc)
    {
        object input;
        if (fcc.Arguments is IDictionary<string, object?> argDict && argDict.Count > 0)
            input = argDict;
        else
            input = new Dictionary<string, object?>();
        var block = new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["id"] = fcc.CallId,
            ["name"] = fcc.Name,
            ["input"] = input
        };
        AttachCacheControl(block, fcc);
        return block;
    }

    private static object BuildToolResultBlock(FunctionResultContent frc)
    {
        var block = new Dictionary<string, object?>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = frc.CallId
        };

        // Structured content (array of {text/image} blocks) is preserved verbatim
        if (frc.Result is JsonElement je
            && (je.ValueKind == JsonValueKind.Array || je.ValueKind == JsonValueKind.Object))
        {
            block["content"] = je;
        }
        else
        {
            block["content"] = frc.Result?.ToString() ?? "";
        }

        if (frc.AdditionalProperties?.TryGetValue("transit.tool_result.is_error", out var isErr) == true
            && isErr is true)
            block["is_error"] = true;

        AttachCacheControl(block, frc);
        return block;
    }

    private static void AttachCacheControl(Dictionary<string, object?> block, AIContent source)
    {
        if (source.AdditionalProperties?.TryGetValue(AnthropicHints.CacheControl, out var cc) == true
            && cc != null)
        {
            block["cache_control"] = cc;
        }
    }

    private static void CopyDocumentMetadata(Dictionary<string, object?> block, AIContent source)
    {
        var props = source.AdditionalProperties;
        if (props == null) return;
        if (props.TryGetValue("transit.anthropic.document.title", out var t) && t is string ts)
            block["title"] = ts;
        if (props.TryGetValue("transit.anthropic.document.context", out var ctx) && ctx is string ctxs)
            block["context"] = ctxs;
        if (props.TryGetValue("transit.anthropic.document.citations", out var cit) && cit is JsonElement citEl)
            block["citations"] = citEl;
    }
}
