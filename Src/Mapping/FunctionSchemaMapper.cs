using System.Text.Json;

namespace Gateway.Shared.ChatTransit.Mapping;

/// <summary>
/// Best-effort JSON Schema normalisation across protocols.
/// Gemini does not support: $ref, $defs, additionalProperties, oneOf, anyOf, allOf with mixed types.
/// When converting to Gemini we degrade gracefully rather than failing.
/// </summary>
public static class FunctionSchemaMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// Normalises a schema <see cref="JsonElement"/> for Gemini consumption.
    /// Strips or inlines unsupported keywords.
    /// Returns null if schema is null/undefined.
    /// </summary>
    public static JsonElement? ToGemini(JsonElement? schema)
    {
        if (schema is null) return null;
        if (schema.Value.ValueKind == JsonValueKind.Null
            || schema.Value.ValueKind == JsonValueKind.Undefined) return null;

        var dict = CloneObject(schema.Value);
        NormaliseForGemini(dict);
        return JsonSerializer.SerializeToElement(dict, JsonOpts);
    }

    /// <summary>
    /// Passes the schema through for OpenAI / Anthropic — both accept the full JSON Schema subset
    /// we generate. Returns the element unchanged.
    /// </summary>
    public static JsonElement? ToOpenAi(JsonElement? schema) => schema;

    public static JsonElement? ToAnthropic(JsonElement? schema) => schema;

    // ── Internal ──────────────────────────────────────────────────────────────

    private static void NormaliseForGemini(Dictionary<string, object?> node)
    {
        // Remove unsupported keywords at this level
        node.Remove("$schema");
        node.Remove("$ref");       // Gemini cannot inline $ref — just drop
        node.Remove("$defs");
        node.Remove("definitions");
        node.Remove("unevaluatedProperties");
        node.Remove("if");
        node.Remove("then");
        node.Remove("else");

        // Gemini does not support additionalProperties:true/false — remove
        // (additionalProperties:{} schema would need recursive treatment; for now just strip)
        if (node.TryGetValue("additionalProperties", out var ap)
            && ap is JsonElement apEl && apEl.ValueKind != JsonValueKind.Object)
        {
            node.Remove("additionalProperties");
        }

        // Flatten single-entry oneOf/anyOf to the entry itself if it is unambiguous
        if (node.TryGetValue("oneOf", out var oneOf) && oneOf is JsonElement oneOfEl
            && oneOfEl.ValueKind == JsonValueKind.Array && oneOfEl.GetArrayLength() == 1)
        {
            var single = CloneObject(oneOfEl.EnumerateArray().First());
            foreach (var kv in single) node[kv.Key] = kv.Value;
            node.Remove("oneOf");
        }
        else if (node.ContainsKey("oneOf"))
        {
            node.Remove("oneOf");
        }

        if (node.ContainsKey("anyOf"))
            node.Remove("anyOf");
        if (node.ContainsKey("allOf"))
            node.Remove("allOf");

        // Recurse into "properties"
        if (node.TryGetValue("properties", out var props) && props is JsonElement propsEl
            && propsEl.ValueKind == JsonValueKind.Object)
        {
            var newProps = new Dictionary<string, object?>();
            foreach (var prop in propsEl.EnumerateObject())
            {
                var propDict = CloneObject(prop.Value);
                NormaliseForGemini(propDict);
                newProps[prop.Name] = JsonSerializer.SerializeToElement(propDict, JsonOpts);
            }
            node["properties"] = JsonSerializer.SerializeToElement(newProps, JsonOpts);
        }

        // Recurse into "items"
        if (node.TryGetValue("items", out var items) && items is JsonElement itemsEl
            && itemsEl.ValueKind == JsonValueKind.Object)
        {
            var itemsDict = CloneObject(itemsEl);
            NormaliseForGemini(itemsDict);
            node["items"] = JsonSerializer.SerializeToElement(itemsDict, JsonOpts);
        }
    }

    private static Dictionary<string, object?> CloneObject(JsonElement el)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (el.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in el.EnumerateObject())
            d[p.Name] = p.Value.Clone();
        return d;
    }
}
