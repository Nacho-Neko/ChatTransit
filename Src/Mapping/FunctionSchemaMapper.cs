using System.Text.Json;

namespace ChatTransit.Mapping;

/// <summary>
/// Best-effort JSON Schema normalisation across protocols.
/// <para>Gemini function-declaration <c>parameters</c> use the OpenAPI-3.0 subset,
/// which <b>does</b> support <c>anyOf</c> (inline), <c>additionalProperties</c>,
/// <c>prefixItems</c>, <c>enum</c>, and numeric bounds, but does <b>not</b> support
/// <c>$ref</c>/<c>$defs</c> (references cannot be inlined here) or <c>allOf</c>.
/// <c>oneOf</c> is folded into <c>anyOf</c> (Gemini treats them identically).</para>
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
        // Remove keywords the OpenAPI-subset `parameters` field cannot express.
        // $ref/$defs can only be inlined via the newer parametersJsonSchema field;
        // here we drop them (best-effort degrade). allOf has no subset equivalent.
        node.Remove("$schema");
        node.Remove("$id");
        node.Remove("$ref");
        node.Remove("$defs");
        node.Remove("definitions");
        node.Remove("unevaluatedProperties");
        node.Remove("if");
        node.Remove("then");
        node.Remove("else");
        node.Remove("allOf");

        // oneOf is accepted as anyOf by Gemini — fold it over so union semantics
        // survive instead of being dropped.
        if (node.Remove("oneOf", out var oneOf) && !node.ContainsKey("anyOf"))
            node["anyOf"] = oneOf;

        // anyOf IS supported (inline): recurse into each branch rather than drop it.
        RecurseArray(node, "anyOf");
        // prefixItems (tuple arrays) IS supported: recurse each positional schema.
        RecurseArray(node, "prefixItems");

        // additionalProperties IS supported (bool or schema). Keep bool as-is; when
        // it is a nested schema object, recurse into it.
        if (node.TryGetValue("additionalProperties", out var ap)
            && ap is JsonElement apEl && apEl.ValueKind == JsonValueKind.Object)
        {
            var apDict = CloneObject(apEl);
            NormaliseForGemini(apDict);
            node["additionalProperties"] = JsonSerializer.SerializeToElement(apDict, JsonOpts);
        }

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

        // Recurse into "items" (single-schema form)
        if (node.TryGetValue("items", out var items) && items is JsonElement itemsEl
            && itemsEl.ValueKind == JsonValueKind.Object)
        {
            var itemsDict = CloneObject(itemsEl);
            NormaliseForGemini(itemsDict);
            node["items"] = JsonSerializer.SerializeToElement(itemsDict, JsonOpts);
        }
    }

    /// <summary>Recurses <see cref="NormaliseForGemini"/> into each object element of
    /// an array-valued keyword (e.g. <c>anyOf</c>, <c>prefixItems</c>).</summary>
    private static void RecurseArray(Dictionary<string, object?> node, string key)
    {
        if (!node.TryGetValue(key, out var val)
            || val is not JsonElement el
            || el.ValueKind != JsonValueKind.Array)
            return;

        var normalised = new List<object?>();
        foreach (var entry in el.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object)
            {
                var dict = CloneObject(entry);
                NormaliseForGemini(dict);
                normalised.Add(JsonSerializer.SerializeToElement(dict, JsonOpts));
            }
            else
            {
                normalised.Add(entry.Clone());
            }
        }
        node[key] = JsonSerializer.SerializeToElement(normalised, JsonOpts);
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
