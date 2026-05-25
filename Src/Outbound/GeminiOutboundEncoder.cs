using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Outbound;

/// <summary>
/// Encodes a <see cref="TransitRequest"/> into Gemini <c>generateContent</c> JSON bytes.
/// <para>Faithfully restores Gemini 3 <c>functionCall.id</c>, <c>thoughtSignature</c>,
/// and the full <c>generationConfig</c> surface (penalties, candidateCount,
/// logprobs, thinkingConfig, mediaResolution, speechConfig). Projects MEAI
/// <see cref="ChatToolMode"/> into <c>toolConfig.functionCallingConfig</c>.</para>
/// </summary>
public sealed class GeminiOutboundEncoder : IRequestEncoder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public ChatTransitProtocol Protocol => ChatTransitProtocol.Gemini;

    public byte[] Encode(TransitRequest request)
    {
        var body = new Dictionary<string, object?>();

        var systemMessages = request.Messages.Where(m => m.Role == ChatRole.System).ToList();
        var userMessages = request.Messages.Where(m => m.Role != ChatRole.System).ToList();

        body["contents"] = BuildContents(userMessages);

        var sysText = FlattenSystem(systemMessages);
        if (!string.IsNullOrEmpty(sysText))
        {
            body["systemInstruction"] = new
            {
                parts = new[] { new { text = sysText } }
            };
        }

        var gc = BuildGenerationConfig(request);
        if (gc.Count > 0) body["generationConfig"] = gc;

        var toolEntries = new List<object>();
        if (request.FunctionTools is { Count: > 0 })
        {
            var decls = request.FunctionTools.Select(t => (object)new
            {
                name = t.Name,
                description = t.Description,
                parameters = FunctionSchemaMapper.ToGemini(t.ParametersSchema)
            }).ToList();
            toolEntries.Add(new { functionDeclarations = decls });
        }
        if (request.Hints.TryGetValue(GeminiHints.BuiltinTools, out var bt)
            && bt is List<JsonElement> btList)
        {
            foreach (var entry in btList)
                toolEntries.Add(entry);
        }
        if (toolEntries.Count > 0) body["tools"] = toolEntries;

        // toolConfig: prefer original Gemini-shape hint, otherwise project from IR
        if (request.Hints.TryGetValue(GeminiHints.ToolConfig, out var tc) && tc is JsonElement tcEl)
        {
            body["toolConfig"] = tcEl;
        }
        else if (request.Options.ToolMode is { } toolMode)
        {
            var projected = ProjectToolMode(toolMode);
            if (projected != null) body["toolConfig"] = projected;
        }

        // Hint passthrough
        if (request.Hints.TryGetValue(GeminiHints.SafetySettings, out var ss) && ss is JsonElement ssEl)
            body["safetySettings"] = ssEl;
        if (request.Hints.TryGetValue(GeminiHints.CachedContent, out var cc) && cc is string ccStr)
            body["cachedContent"] = ccStr;
        if (request.Hints.TryGetValue(GeminiHints.Labels, out var lbl) && lbl is JsonElement lblEl)
            body["labels"] = lblEl;

        return JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? FlattenSystem(IList<ChatMessage> systemMessages)
    {
        if (systemMessages.Count == 0) return null;
        var parts = systemMessages
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Where(t => !ThinkingMapper.IsThinkingContent(t))
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));
        return string.Join("\n\n", parts);
    }

    private static object? ProjectToolMode(ChatToolMode mode) => mode switch
    {
        NoneChatToolMode => new
        {
            functionCallingConfig = new { mode = "NONE" }
        },
        AutoChatToolMode => new
        {
            functionCallingConfig = new { mode = "AUTO" }
        },
        RequiredChatToolMode r when !string.IsNullOrEmpty(r.RequiredFunctionName)
            => new
            {
                functionCallingConfig = new
                {
                    mode = "ANY",
                    allowedFunctionNames = new[] { r.RequiredFunctionName! }
                }
            },
        RequiredChatToolMode => new
        {
            functionCallingConfig = new { mode = "ANY" }
        },
        _ => null
    };

    private static List<object> BuildContents(IList<ChatMessage> messages)
    {
        var result = new List<object>();
        foreach (var msg in messages)
        {
            var role = msg.Role == ChatRole.Assistant ? "model" : "user";
            var parts = BuildParts(msg.Contents);
            if (parts.Count > 0)
                result.Add(new { role, parts });
        }
        return result;
    }

    private static List<object> BuildParts(IList<AIContent> contents)
    {
        var parts = new List<object>();
        foreach (var c in contents)
        {
            // Pattern-matched switch can't use `case _ when` so check thinking first
            if (ThinkingMapper.IsThinkingContent(c))
            {
                parts.Add(BuildThoughtPart(c));
                continue;
            }

            switch (c)
            {
                case TextContent tc when tc.AdditionalProperties?
                        .TryGetValue("transit.gemini.raw_part", out var raw) == true
                    && raw is string rawJson:
                    // Restore opaque parts (executableCode / codeExecutionResult) byte-for-byte
                    try
                    {
                        using var doc = JsonDocument.Parse(rawJson);
                        parts.Add(doc.RootElement.Clone());
                    }
                    catch { parts.Add(new { text = tc.Text ?? "" }); }
                    break;

                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    parts.Add(new { text = tc.Text });
                    break;

                case DataContent dc:
                    var inline = MultimodalContentMapper.ToGeminiInline(dc);
                    if (inline != null)
                    {
                        var part = new Dictionary<string, object?>
                        {
                            ["inlineData"] = new { mimeType = inline.MimeType, data = inline.Base64Data }
                        };
                        AttachVideoMetadata(part, dc);
                        parts.Add(part);
                    }
                    break;

                case UriContent uc:
                    var file = MultimodalContentMapper.ToGeminiFile(uc);
                    if (file != null)
                    {
                        var part = new Dictionary<string, object?>
                        {
                            ["fileData"] = new { mimeType = file.MimeType, fileUri = file.FileUri }
                        };
                        AttachVideoMetadata(part, uc);
                        parts.Add(part);
                    }
                    break;

                case FunctionCallContent fcc:
                    parts.Add(BuildFunctionCallPart(fcc));
                    break;

                case FunctionResultContent frc:
                    parts.Add(BuildFunctionResponsePart(frc));
                    break;
            }
        }
        return parts;
    }

    private static object BuildThoughtPart(AIContent content)
    {
        var text = ThinkingMapper.GetThinkingText(content) ?? "";
        var sig = ThinkingMapper.GetGeminiThoughtSignature(content);
        var part = new Dictionary<string, object?>
        {
            ["thought"] = true,
            ["text"] = text
        };
        if (!string.IsNullOrEmpty(sig)) part["thoughtSignature"] = sig;
        return part;
    }

    private static object BuildFunctionCallPart(FunctionCallContent fcc)
    {
        var fc = new Dictionary<string, object?>
        {
            ["name"] = fcc.Name
        };

        // Emit explicit id only when:
        //  - the original payload had an id (Gemini 3 → Gemini 3 round-trip), OR
        //  - the CallId differs from Name (cross-protocol from OpenAI/Anthropic
        //    where CallId is a real unique id like "call_xxx" or "toolu_xxx")
        // This keeps Gemini 1.5/2 requests (where Name == CallId) clean.
        var hasGeminiId = fcc.AdditionalProperties?.TryGetValue("transit.gemini.has_id", out var v) == true
                          && v is true;
        var differs = !string.Equals(fcc.CallId, fcc.Name, StringComparison.Ordinal)
                      && !string.IsNullOrEmpty(fcc.CallId);
        if (hasGeminiId || differs)
            fc["id"] = fcc.CallId;

        fc["args"] = fcc.Arguments ?? (object)new Dictionary<string, object?>();

        var part = new Dictionary<string, object?>
        {
            ["functionCall"] = fc
        };

        // Preserve any thoughtSignature attached to the function call
        var ts = ThinkingMapper.GetGeminiThoughtSignature(fcc);
        if (!string.IsNullOrEmpty(ts)) part["thoughtSignature"] = ts;

        return part;
    }

    private static object BuildFunctionResponsePart(FunctionResultContent frc)
    {
        // Determine the real function name. The CallId may be an opaque id
        // (Gemini 3) — in that case we stored the original name in additional props.
        string? funcName = null;
        if (frc.AdditionalProperties?.TryGetValue("transit.gemini.function_name", out var fn) == true
            && fn is string fnStr)
            funcName = fnStr;
        funcName ??= frc.CallId;

        var hasGeminiId = frc.AdditionalProperties?.TryGetValue("transit.gemini.has_id", out var v) == true
                          && v is true;
        var differs = !string.Equals(frc.CallId, funcName, StringComparison.Ordinal);

        var fr = new Dictionary<string, object?>
        {
            ["name"] = funcName
        };
        if (hasGeminiId || differs)
            fr["id"] = frc.CallId;

        fr["response"] = NormaliseResponse(frc.Result);

        return new Dictionary<string, object?>
        {
            ["functionResponse"] = fr
        };
    }

    private static object NormaliseResponse(object? raw) => raw switch
    {
        null => new Dictionary<string, object?>(),
        JsonElement je when je.ValueKind == JsonValueKind.Object => je,
        JsonElement je when je.ValueKind == JsonValueKind.Array => new Dictionary<string, object?> { ["result"] = je },
        JsonElement je => new Dictionary<string, object?> { ["result"] = je },
        string s => TryParseJsonObject(s) ?? new Dictionary<string, object?> { ["result"] = s },
        IDictionary<string, object?> d => d,
        _ => new Dictionary<string, object?> { ["result"] = raw }
    };

    private static IDictionary<string, object?>? TryParseJsonObject(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }
        catch { return null; }
    }

    private static void AttachVideoMetadata(Dictionary<string, object?> part, AIContent source)
    {
        if (source.AdditionalProperties?.TryGetValue(GeminiHints.VideoMetadata, out var vm) == true
            && vm is JsonElement vmEl)
        {
            part["videoMetadata"] = vmEl;
        }
    }

    private static Dictionary<string, object?> BuildGenerationConfig(TransitRequest request)
    {
        var gc = new Dictionary<string, object?>();
        var opts = request.Options;

        if (opts.Temperature.HasValue)
            gc["temperature"] = SamplingScaleMapper.ClampTemperatureForOpenAiScale(opts.Temperature.Value);
        if (opts.TopP.HasValue) gc["topP"] = SamplingScaleMapper.ClampTopP(opts.TopP.Value);
        if (opts.TopK.HasValue) gc["topK"] = SamplingScaleMapper.ClampTopK(opts.TopK.Value);
        if (opts.MaxOutputTokens.HasValue) gc["maxOutputTokens"] = opts.MaxOutputTokens.Value;
        if (opts.StopSequences is { Count: > 0 }) gc["stopSequences"] = opts.StopSequences;
        if (opts.Seed.HasValue) gc["seed"] = opts.Seed.Value;
        if (opts.PresencePenalty.HasValue) gc["presencePenalty"] = Math.Clamp(opts.PresencePenalty.Value, -2f, 2f);
        if (opts.FrequencyPenalty.HasValue) gc["frequencyPenalty"] = Math.Clamp(opts.FrequencyPenalty.Value, -2f, 2f);

        if (request.Hints.TryGetValue(GeminiHints.ResponseMimeType, out var rmt) && rmt is string rmtStr)
            gc["responseMimeType"] = rmtStr;
        if (request.Hints.TryGetValue(GeminiHints.ResponseSchema, out var rs) && rs is JsonElement rsEl)
            gc["responseSchema"] = rsEl;
        if (request.Hints.TryGetValue(GeminiHints.ResponseJsonSchema, out var rjs) && rjs is JsonElement rjsEl)
            gc["responseJsonSchema"] = rjsEl;

        // Cross-protocol: project OpenAI `response_format` onto Gemini equivalents
        // when Gemini hints aren't already set. Officially documented mapping:
        //   {type:"json_schema", json_schema:{schema:{...}}} → responseSchema + responseMimeType
        //   {type:"json_object"}                            → responseMimeType only
        // (https://ai.google.dev/api/generate-content#GenerationConfig.responseSchema)
        if (!gc.ContainsKey("responseSchema") && !gc.ContainsKey("responseMimeType")
            && request.Hints.TryGetValue(OpenAiHints.ResponseFormat, out var orf)
            && orf is JsonElement orfEl
            && orfEl.ValueKind == JsonValueKind.Object)
        {
            var rfType = orfEl.TryGetProperty("type", out var rfTypeEl) ? rfTypeEl.GetString() : null;
            if (rfType == "json_schema"
                && orfEl.TryGetProperty("json_schema", out var jsObj)
                && jsObj.ValueKind == JsonValueKind.Object
                && jsObj.TryGetProperty("schema", out var schemaEl))
            {
                gc["responseMimeType"] = "application/json";
                gc["responseSchema"] = FunctionSchemaMapper.ToGemini(schemaEl);
            }
            else if (rfType == "json_object")
            {
                gc["responseMimeType"] = "application/json";
            }
        }
        if (request.Hints.TryGetValue(GeminiHints.ResponseModalities, out var rm) && rm is JsonElement rmEl)
            gc["responseModalities"] = rmEl;
        if (request.Hints.TryGetValue(GeminiHints.CandidateCount, out var cn) && cn is int cnv)
            gc["candidateCount"] = cnv;
        if (request.Hints.TryGetValue(GeminiHints.ResponseLogprobs, out var rl) && rl is true)
            gc["responseLogprobs"] = true;
        if (request.Hints.TryGetValue(GeminiHints.Logprobs, out var lp) && lp is int lpv)
            gc["logprobs"] = lpv;
        if (request.Hints.TryGetValue(GeminiHints.AudioTimestamp, out var at) && at is true)
            gc["audioTimestamp"] = true;
        if (request.Hints.TryGetValue(GeminiHints.MediaResolution, out var mr) && mr is string mrStr)
            gc["mediaResolution"] = mrStr;
        if (request.Hints.TryGetValue(GeminiHints.SpeechConfig, out var sc) && sc is JsonElement scEl)
            gc["speechConfig"] = scEl;

        var thinkingCfg = new Dictionary<string, object?>();
        if (request.Hints.TryGetValue(GeminiHints.ThinkingBudget, out var tb) && tb is int tbVal)
            thinkingCfg["thinkingBudget"] = tbVal;
        if (request.Hints.TryGetValue(GeminiHints.ThinkingLevel, out var tl) && tl is string tlStr)
            thinkingCfg["thinkingLevel"] = tlStr;
        // Cross-protocol: project OpenAI's reasoning_effort onto thinkingLevel
        // when no explicit Gemini-shape hint is present. Gemini 3 accepts
        // "low"/"medium"/"high" verbatim; "minimal" downgrades to "low".
        if (!thinkingCfg.ContainsKey("thinkingLevel") && !thinkingCfg.ContainsKey("thinkingBudget")
            && request.Hints.TryGetValue(OpenAiHints.ReasoningEffort, out var re) && re is string reStr)
        {
            thinkingCfg["thinkingLevel"] = reStr.ToLowerInvariant() switch
            {
                "minimal" => "low",
                _ => reStr
            };
        }
        if (request.Hints.TryGetValue(GeminiHints.IncludeThoughts, out var inc) && inc is true)
            thinkingCfg["includeThoughts"] = true;
        if (thinkingCfg.Count > 0) gc["thinkingConfig"] = thinkingCfg;

        return gc;
    }
}
