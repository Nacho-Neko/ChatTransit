using ChatTransit.Abstractions;
using ChatTransit.Hints;
using ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ChatTransit.Outbound;

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
            var decls = request.FunctionTools.Select(t => BuildFunctionDeclaration(t)).ToList();
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

    private static object BuildFunctionDeclaration(TransitFunctionToolDef t)
    {
        // A no-arg tool (or a free-form map) has to go out with no `parameters` at all:
        // Gemini 400s on an OBJECT schema whose properties are empty. See
        // FunctionSchemaMapper.DeclaresNoParameters.
        if (FunctionSchemaMapper.DeclaresNoParameters(t.ParametersSchema))
        {
            return new
            {
                name = t.Name,
                description = t.Description
            };
        }

        // Schemas using $ref/$defs/allOf/if-then-else can't be expressed in the
        // legacy OpenAPI-subset `parameters` field — the sanitizer would drop the
        // referenced subschemas and collapse them to `{}`, losing type/enum/required.
        // Route those through `parametersJsonSchema`, which accepts full JSON Schema.
        if (FunctionSchemaMapper.RequiresJsonSchema(t.ParametersSchema))
        {
            return new
            {
                name = t.Name,
                description = t.Description,
                parametersJsonSchema = t.ParametersSchema
            };
        }
        return new
        {
            name = t.Name,
            description = t.Description,
            parameters = FunctionSchemaMapper.ToGemini(t.ParametersSchema)
        };
    }

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
        // Cross-protocol name recovery: Anthropic tool_result / OpenAI tool
        // messages carry only the call id — the function name lives solely on
        // the originating call. Map every call id to its name up front so
        // BuildFunctionResponsePart can restore it (see there for why).
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fcc in messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            if (!string.IsNullOrEmpty(fcc.CallId) && !string.IsNullOrEmpty(fcc.Name))
                callNames.TryAdd(fcc.CallId, fcc.Name);
        }

        var result = new List<object>();
        foreach (var msg in messages)
        {
            var role = msg.Role == ChatRole.Assistant ? "model" : "user";
            var parts = BuildParts(msg.Contents, callNames);

            // A message can map to zero parts: empty/whitespace text, an image
            // whose base64 the inbound decoder rejected, or a content type this
            // encoder has no part shape for. Dropping the turn would silently
            // change the SHAPE of the conversation, and Gemini validates shape:
            // the last non-empty turn may not be `model` (Gemini 3+ answers a
            // trailing model turn with 400 "Requests ending with a model turn
            // are not supported."). Losing the final user turn is therefore
            // enough to turn a perfectly valid request into a rejected prefill.
            // Keep the role slot with an explicit empty part so the loss stays
            // visible and the target side can normalise it deliberately.
            if (parts.Count == 0)
                parts.Add(new { text = "" });

            result.Add(new { role, parts });
        }
        return result;
    }

    private static List<object> BuildParts(
        IList<AIContent> contents,
        IReadOnlyDictionary<string, string> callNames)
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
                    parts.Add(BuildTextPart(tc));
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
                    parts.Add(BuildFunctionResponsePart(frc, callNames));
                    break;
            }
        }
        return parts;
    }

    private static object BuildThoughtPart(AIContent content)
    {
        var text = ThinkingMapper.GetThinkingText(content) ?? "";
        // Cross-protocol round-trip: a non-Gemini caller (Claude Code on the
        // Anthropic API, or an OpenAI-format client) routed onto a Gemini-native
        // backend (Antigravity → PA → Claude/Vertex) carries the opaque thinking
        // signature under that protocol's own key. The blob is the SAME value the
        // upstream tunnels as thoughtSignature, so recover it from any carrier.
        // Dropping it makes PA emit a signature-less Anthropic thinking block and
        // the Vertex adapter 400s with
        // "messages.N.content.0.thinking.signature: Field required".
        var sig = ThinkingMapper.GetAnySignature(content);
        var part = new Dictionary<string, object?>
        {
            ["thought"] = true,
            ["text"] = text
        };
        if (!string.IsNullOrEmpty(sig)) part["thoughtSignature"] = sig;
        return part;
    }

    private static object BuildTextPart(TextContent tc)
    {
        // Gemini 3 can sign an ordinary text part (typically the last one of a
        // response). GeminiInboundDecoder keeps that signature on the content;
        // echo it back on the same part. Only the Gemini-native carrier counts —
        // an Anthropic/OpenAI blob recovered via GetAnySignature would be a
        // foreign value in a field Gemini interprets, so it is deliberately not
        // used here (unlike thought parts, where the blob is the same tunnel).
        var sig = ThinkingMapper.GetGeminiThoughtSignature(tc);
        if (string.IsNullOrEmpty(sig))
            return new { text = tc.Text };

        return new Dictionary<string, object?>
        {
            ["text"] = tc.Text,
            ["thoughtSignature"] = sig,
        };
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

    private static object BuildFunctionResponsePart(
        FunctionResultContent frc,
        IReadOnlyDictionary<string, string> callNames)
    {
        // Determine the real function name. The CallId may be an opaque id
        // (Gemini 3) — in that case we stored the original name in additional props.
        string? funcName = null;
        if (frc.AdditionalProperties?.TryGetValue("transit.gemini.function_name", out var fn) == true
            && fn is string fnStr)
            funcName = fnStr;
        // Cross-protocol (Anthropic tool_result / OpenAI tool message): the
        // inbound block has no function name, only the call id. Recover the
        // name from the matching FunctionCallContent. Without this the id was
        // emitted as `name` with no `id` field, so id-pairing downstream
        // (e.g. the Anthropic-Vertex adapter behind Antigravity PA) minted a
        // fresh mismatched tool_use_id and the upstream rejected the request.
        if (funcName is null && !string.IsNullOrEmpty(frc.CallId)
            && callNames.TryGetValue(frc.CallId, out var mapped))
            funcName = mapped;
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
                // Same $ref/$defs loss as function tools: route full-JSON-Schema
                // response formats through responseJsonSchema instead of the
                // OpenAPI-subset responseSchema (the two are mutually exclusive).
                if (FunctionSchemaMapper.RequiresJsonSchema(schemaEl))
                    gc["responseJsonSchema"] = schemaEl.Clone();
                else
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

        var thinkingCfg = BuildThinkingConfig(request);
        if (thinkingCfg.Count > 0) gc["thinkingConfig"] = thinkingCfg;

        return gc;
    }

    /// <summary>
    /// Builds <c>thinkingConfig</c>, emitting <b>exactly one</b> of
    /// <c>thinkingLevel</c> / <c>thinkingBudget</c> — sending both is a 400.
    /// Gemini 3+ takes <c>thinkingLevel</c>; Gemini 2.5 takes the legacy
    /// <c>thinkingBudget</c>. The caller's intent is read from whichever hint is
    /// present (native Gemini budget/level, or a cross-protocol OpenAI
    /// <c>reasoning_effort</c>) and converted to the field the target accepts.
    /// </summary>
    private static Dictionary<string, object?> BuildThinkingConfig(TransitRequest request)
    {
        var cfg = new Dictionary<string, object?>();

        int? budget = request.Hints.TryGetValue(GeminiHints.ThinkingBudget, out var tb) && tb is int tbv
            ? tbv : null;
        string? level = request.Hints.TryGetValue(GeminiHints.ThinkingLevel, out var tl) && tl is string tlStr
            ? tlStr : null;
        // Cross-protocol: OpenAI reasoning_effort expresses the same intent as a level.
        if (level is null && budget is null
            && request.Hints.TryGetValue(OpenAiHints.ReasoningEffort, out var re) && re is string reStr)
            level = reStr;

        if (ModelCapabilities.GeminiSupportsThinkingLevel(request.Model))
        {
            // Gemini 3+: thinkingLevel only. Normalise so invalid enum values
            // (xhigh/max/none) never reach the wire; derive from a budget hint
            // when that's all we have.
            var norm = ReasoningEffortMapper.ToGeminiLevel(level)
                       ?? ReasoningEffortMapper.BudgetToGeminiLevel(budget);
            if (norm != null) cfg["thinkingLevel"] = norm;
        }
        else
        {
            // Gemini 2.5: thinkingBudget only.
            var b = budget ?? ReasoningEffortMapper.EffortToBudget(level);
            if (b.HasValue) cfg["thinkingBudget"] = b.Value;
        }

        if (request.Hints.TryGetValue(GeminiHints.IncludeThoughts, out var inc) && inc is true)
            cfg["includeThoughts"] = true;

        return cfg;
    }
}
