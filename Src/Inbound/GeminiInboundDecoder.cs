using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Hints;
using Gateway.Shared.ChatTransit.Mapping;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Inbound;

/// <summary>
/// Decodes Google Gemini <c>generateContent</c> JSON into a <see cref="TransitRequest"/>.
/// <para>Preserves the Gemini 3 per-call <c>id</c> on functionCall parts and the
/// <c>thoughtSignature</c> on thought parts (both required for thinking continuity
/// per the official function-calling docs). Also captures the full
/// <c>generationConfig</c> surface (penalties, logprobs, candidateCount,
/// responseModalities, thinkingConfig, mediaResolution, speechConfig).</para>
/// </summary>
public sealed class GeminiInboundDecoder : IRequestDecoder
{
    public ChatTransitProtocol Protocol => ChatTransitProtocol.Gemini;

    public TransitRequest Decode(byte[] requestBytes, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(requestBytes);
        var root = doc.RootElement;

        // Gemini uses URL params (`?alt=sse`, `:streamGenerateContent`) for stream
        // selection rather than a body field. The transport layer is responsible
        // for setting Stream after parsing; we default to false here.
        const bool stream = false;

        var messages = DecodeContents(root);
        var options = new ChatOptions();
        ApplyScalars(root, options);
        var hints = new Dictionary<string, object?>(StringComparer.Ordinal);
        ApplyToolChoice(root, options, hints);
        BuildHints(root, hints);
        var tools = DecodeTools(root, hints);

        return new TransitRequest
        {
            Messages = messages,
            Options = options,
            Hints = hints,
            Model = "",   // Gemini model is in the URL path, not the body
            Stream = stream,
            FunctionTools = tools.Count > 0 ? tools : null
        };
    }

    // ?? Content decoding ??????????????????????????????????????????????????????

    private static IList<ChatMessage> DecodeContents(JsonElement root)
    {
        var result = new List<ChatMessage>();

        if (root.TryGetProperty("systemInstruction", out var si) && si.ValueKind == JsonValueKind.Object)
        {
            var sysText = ExtractTextFromContent(si);
            if (!string.IsNullOrEmpty(sysText))
                result.Add(new ChatMessage(ChatRole.System, sysText));
        }

        if (!root.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var content in contents.EnumerateArray())
        {
            var roleStr = content.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            var role = roleStr.Equals("model", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;

            var aiContents = DecodeParts(content);
            result.Add(new ChatMessage(role, aiContents));
        }

        return result;
    }

    private static List<AIContent> DecodeParts(JsonElement contentEl)
    {
        var contents = new List<AIContent>();
        if (!contentEl.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return contents;

        foreach (var part in parts.EnumerateArray())
        {
            // Thought part ? may carry a thoughtSignature that MUST be returned
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
            {
                var text = part.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                var thoughtSig = part.TryGetProperty("thoughtSignature", out var ts)
                    ? ts.GetString() : null;
                var thinking = ThinkingMapper.CreateThinkingContent(
                    text, geminiSignature: thoughtSig);
                contents.Add(thinking);
                continue;
            }

            // functionCall ? Gemini 3 attaches a unique id we MUST round-trip
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var id = fc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                IDictionary<string, object?>? args = null;
                if (fc.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                {
                    args = new Dictionary<string, object?>();
                    foreach (var p2 in argsEl.EnumerateObject())
                        ((Dictionary<string, object?>)args)[p2.Name] = p2.Value.Clone();
                }
                // CallId precedence: explicit id (Gemini 3) > name (Gemini 1.5/2 fallback).
                // We also record whether the id was actually present so the encoder
                // knows whether to emit it on Gemini ? Gemini round-trip.
                var callId = !string.IsNullOrEmpty(id) ? id! : name;
                var fcc = new FunctionCallContent(callId, name, args);
                if (!string.IsNullOrEmpty(id))
                {
                    fcc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    fcc.AdditionalProperties["transit.gemini.has_id"] = true;
                }
                // Preserve any thoughtSignature attached to the FunctionCall (Gemini 3
                // sometimes attaches signatures to tool-call parts directly).
                if (part.TryGetProperty("thoughtSignature", out var fcTs)
                    && fcTs.GetString() is { Length: > 0 } fcSig)
                {
                    fcc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    fcc.AdditionalProperties[ThinkingMapper.GeminiThoughtSignature] = fcSig;
                }
                contents.Add(fcc);
                continue;
            }

            // functionResponse ? user ? model tool result
            if (part.TryGetProperty("functionResponse", out var fr))
            {
                var name = fr.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var id = fr.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                object? response = null;
                if (fr.TryGetProperty("response", out var resp))
                {
                    response = resp.ValueKind == JsonValueKind.Object
                        ? (object)resp.Clone()
                        : resp.ToString();
                }
                var callId = !string.IsNullOrEmpty(id) ? id! : name;
                var frc = new FunctionResultContent(callId, response);
                frc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                frc.AdditionalProperties["transit.gemini.function_name"] = name;
                if (!string.IsNullOrEmpty(id))
                    frc.AdditionalProperties["transit.gemini.has_id"] = true;
                contents.Add(frc);
                continue;
            }

            // Plain text part
            if (part.TryGetProperty("text", out var textEl) && textEl.GetString() is { Length: > 0 } txt)
            {
                contents.Add(new TextContent(txt));
                continue;
            }

            // inlineData (image / PDF / audio / video / etc.)
            if (part.TryGetProperty("inlineData", out var inlineData))
            {
                var mimeType = inlineData.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "";
                var data = inlineData.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
                if (data.Length > 0)
                {
                    var bytes = Convert.FromBase64String(data);
                    var dc = new DataContent(bytes, mimeType);
                    if (part.TryGetProperty("videoMetadata", out var vm) && vm.ValueKind == JsonValueKind.Object)
                    {
                        dc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                        dc.AdditionalProperties[GeminiHints.VideoMetadata] = vm.Clone();
                    }
                    contents.Add(dc);
                }
                continue;
            }

            // fileData (GCS URI / HTTP URL)
            if (part.TryGetProperty("fileData", out var fileData))
            {
                var mimeType = fileData.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "";
                var uri = fileData.TryGetProperty("fileUri", out var u) ? u.GetString() ?? "" : "";
                if (uri.Length > 0)
                {
                    var uc = new UriContent(uri, mimeType);
                    if (part.TryGetProperty("videoMetadata", out var vm) && vm.ValueKind == JsonValueKind.Object)
                    {
                        uc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                        uc.AdditionalProperties[GeminiHints.VideoMetadata] = vm.Clone();
                    }
                    contents.Add(uc);
                }
                continue;
            }

            // executableCode / codeExecutionResult ? code execution tool round-trip
            if (part.TryGetProperty("executableCode", out var execCode))
            {
                var raw = new TextContent("[executable_code]");
                raw.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                raw.AdditionalProperties["transit.gemini.raw_part"] = part.GetRawText();
                raw.AdditionalProperties["transit.gemini.raw_part_type"] = "executableCode";
                contents.Add(raw);
                continue;
            }
            if (part.TryGetProperty("codeExecutionResult", out var codeRes))
            {
                var raw = new TextContent("[code_execution_result]");
                raw.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                raw.AdditionalProperties["transit.gemini.raw_part"] = part.GetRawText();
                raw.AdditionalProperties["transit.gemini.raw_part_type"] = "codeExecutionResult";
                contents.Add(raw);
                continue;
            }
        }

        return contents;
    }

    private static string ExtractTextFromContent(JsonElement contentEl)
    {
        if (!contentEl.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join("\n", parts.EnumerateArray()
            .Where(p => p.TryGetProperty("text", out var _))
            .Select(p => p.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "")
            .Where(t => t.Length > 0));
    }

    // ?? Options ???????????????????????????????????????????????????????????????

    private static void ApplyScalars(JsonElement root, ChatOptions options)
    {
        if (!root.TryGetProperty("generationConfig", out var gc) || gc.ValueKind != JsonValueKind.Object)
            return;

        // Gemini 1.5+/2.x temperature is [0, 2] ? same as the IR convention.
        if (gc.TryGetProperty("temperature", out var temp) && temp.TryGetDouble(out var t))
            options.Temperature = SamplingScaleMapper.ClampTemperatureForOpenAiScale((float)t);
        if (gc.TryGetProperty("topP", out var topP) && topP.TryGetDouble(out var tp))
            options.TopP = SamplingScaleMapper.ClampTopP((float)tp);
        if (gc.TryGetProperty("topK", out var topK) && topK.TryGetInt32(out var tkv))
            options.TopK = SamplingScaleMapper.ClampTopK(tkv);
        if (gc.TryGetProperty("maxOutputTokens", out var mo) && mo.TryGetInt32(out var moi))
            options.MaxOutputTokens = moi;
        if (gc.TryGetProperty("stopSequences", out var ss) && ss.ValueKind == JsonValueKind.Array)
        {
            options.StopSequences = ss.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList();
        }
        if (gc.TryGetProperty("seed", out var seed) && seed.TryGetInt64(out var seedVal))
            options.Seed = seedVal;
        if (gc.TryGetProperty("presencePenalty", out var pp) && pp.TryGetDouble(out var ppv))
            options.PresencePenalty = (float)Math.Clamp(ppv, -2.0, 2.0);
        if (gc.TryGetProperty("frequencyPenalty", out var fp) && fp.TryGetDouble(out var fpv))
            options.FrequencyPenalty = (float)Math.Clamp(fpv, -2.0, 2.0);
    }

    private static void ApplyToolChoice(JsonElement root, ChatOptions options,
        Dictionary<string, object?> hints)
    {
        if (!root.TryGetProperty("toolConfig", out var tc) || tc.ValueKind != JsonValueKind.Object)
            return;

        // Raw passthrough for Gemini ? Gemini
        hints[GeminiHints.ToolConfig] = tc.Clone();

        // Canonicalise into ChatToolMode for cross-protocol routing
        if (tc.TryGetProperty("functionCallingConfig", out var fcc)
            && fcc.ValueKind == JsonValueKind.Object)
        {
            var mode = fcc.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : null;
            options.ToolMode = mode?.ToUpperInvariant() switch
            {
                "AUTO" => ChatToolMode.Auto,
                "ANY" => ChatToolMode.RequireAny,
                "NONE" => ChatToolMode.None,
                "VALIDATED" => ChatToolMode.RequireAny, // closest analogue
                _ => options.ToolMode
            };

            // If "allowed_function_names" has exactly one entry, pin to specific
            if (fcc.TryGetProperty("allowedFunctionNames", out var afn)
                && afn.ValueKind == JsonValueKind.Array
                && afn.GetArrayLength() == 1)
            {
                var only = afn[0].GetString();
                if (!string.IsNullOrEmpty(only))
                    options.ToolMode = ChatToolMode.RequireSpecific(only);
            }
        }
    }

    private static IList<TransitFunctionToolDef> DecodeTools(JsonElement root, Dictionary<string, object?> hints)
    {
        var toolList = new List<TransitFunctionToolDef>();
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return toolList;

        var builtins = new List<JsonElement>();
        foreach (var toolset in tools.EnumerateArray())
        {
            if (toolset.ValueKind != JsonValueKind.Object) continue;

            // A single `tools[]` entry may bundle both `functionDeclarations` and
            // built-in retrievers (`googleSearch`, `urlContext`, etc.) ? split them
            // so cross-protocol routes only see real functions, while same-protocol
            // round-trip can replay the built-ins verbatim.
            var hasFunctions = toolset.TryGetProperty("functionDeclarations", out var decls)
                && decls.ValueKind == JsonValueKind.Array;
            var builtinFields = new List<JsonProperty>();
            foreach (var prop in toolset.EnumerateObject())
            {
                if (prop.Name == "functionDeclarations") continue;
                builtinFields.Add(prop);
            }

            if (hasFunctions)
            {
                foreach (var decl in decls.EnumerateArray())
                {
                    var name = decl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    var desc = decl.TryGetProperty("description", out var d) ? d.GetString() : null;
                    var schema = decl.TryGetProperty("parameters", out var p) ? (JsonElement?)p.Clone() : null;
                    toolList.Add(new TransitFunctionToolDef { Name = name, Description = desc, ParametersSchema = schema });
                }
            }

            if (builtinFields.Count > 0)
            {
                // Re-pack the built-in entries into a fresh JsonElement so we can
                // splice them back into tools[] verbatim at encode time.
                var clone = new Dictionary<string, object?>();
                foreach (var p in builtinFields)
                    clone[p.Name] = p.Value.Clone();
                builtins.Add(JsonSerializer.SerializeToElement(clone));
            }
        }

        if (builtins.Count > 0)
            hints[GeminiHints.BuiltinTools] = builtins;

        return toolList;
    }

    private static void BuildHints(JsonElement root, Dictionary<string, object?> hints)
    {
        if (root.TryGetProperty("safetySettings", out var ss) && ss.ValueKind == JsonValueKind.Array)
            hints[GeminiHints.SafetySettings] = ss.Clone();

        if (root.TryGetProperty("cachedContent", out var cc) && cc.GetString() is { } ccv)
            hints[GeminiHints.CachedContent] = ccv;

        if (root.TryGetProperty("labels", out var lbl) && lbl.ValueKind == JsonValueKind.Object)
            hints[GeminiHints.Labels] = lbl.Clone();

        if (root.TryGetProperty("generationConfig", out var gc) && gc.ValueKind == JsonValueKind.Object)
        {
            if (gc.TryGetProperty("responseMimeType", out var rmt) && rmt.GetString() is { } rmtv)
                hints[GeminiHints.ResponseMimeType] = rmtv;
            if (gc.TryGetProperty("responseSchema", out var rs) && rs.ValueKind != JsonValueKind.Null)
                hints[GeminiHints.ResponseSchema] = rs.Clone();
            if (gc.TryGetProperty("responseJsonSchema", out var rjs) && rjs.ValueKind != JsonValueKind.Null)
                hints[GeminiHints.ResponseJsonSchema] = rjs.Clone();
            if (gc.TryGetProperty("responseModalities", out var rm) && rm.ValueKind == JsonValueKind.Array)
                hints[GeminiHints.ResponseModalities] = rm.Clone();
            if (gc.TryGetProperty("candidateCount", out var cn) && cn.TryGetInt32(out var cnv))
                hints[GeminiHints.CandidateCount] = cnv;
            if (gc.TryGetProperty("responseLogprobs", out var rl) && rl.ValueKind == JsonValueKind.True)
                hints[GeminiHints.ResponseLogprobs] = true;
            if (gc.TryGetProperty("logprobs", out var lp) && lp.TryGetInt32(out var lpv))
                hints[GeminiHints.Logprobs] = lpv;
            if (gc.TryGetProperty("audioTimestamp", out var at) && at.ValueKind == JsonValueKind.True)
                hints[GeminiHints.AudioTimestamp] = true;
            if (gc.TryGetProperty("mediaResolution", out var mr) && mr.GetString() is { } mrv)
                hints[GeminiHints.MediaResolution] = mrv;
            if (gc.TryGetProperty("speechConfig", out var sc) && sc.ValueKind == JsonValueKind.Object)
                hints[GeminiHints.SpeechConfig] = sc.Clone();
            if (gc.TryGetProperty("thinkingConfig", out var thinking)
                && thinking.ValueKind == JsonValueKind.Object)
            {
                if (thinking.TryGetProperty("thinkingBudget", out var budget)
                    && budget.TryGetInt32(out var budgetVal))
                    hints[GeminiHints.ThinkingBudget] = budgetVal;
                if (thinking.TryGetProperty("thinkingLevel", out var lvl)
                    && lvl.GetString() is { } lvlVal)
                    hints[GeminiHints.ThinkingLevel] = lvlVal;
                if (thinking.TryGetProperty("includeThoughts", out var inc)
                    && inc.ValueKind == JsonValueKind.True)
                    hints[GeminiHints.IncludeThoughts] = true;
            }
        }
    }
}
