using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatTransit.Mapping;

/// <summary>
/// Translates a tool's parameter schema between the dialects the four protocols
/// document.
///
/// <para>Three of them (OpenAI Chat, OpenAI Responses, Anthropic) specify the field as
/// plain JSON Schema, so between those there is nothing to translate. Gemini is the odd
/// one out: it has <i>two</i> mutually exclusive fields for the same thing.
/// <c>parameters</c> is typed <c>Schema</c> — "Reflects the Open API 3.03 Parameter
/// Object" — and renders as proto3 JSON, so it supports <c>anyOf</c> (inline),
/// <c>additionalProperties</c>, <c>prefixItems</c>, <c>enum</c> and numeric bounds, but
/// has no place for <c>$ref</c>/<c>$defs</c> or <c>allOf</c>, and spells types, nulls,
/// references and int64s its own way. <c>parametersJsonSchema</c> takes standard JSON
/// Schema as written.</para>
///
/// <para>So the Gemini direction is a choice of field, not a lossy filter:
/// <see cref="ExceedsGeminiSchemaSubset"/> picks the one that can carry the caller's
/// schema, <see cref="ToGemini"/> converts for the legacy field, and
/// <see cref="FromGemini"/> converts back on the way in. <c>oneOf</c> is folded into
/// <c>anyOf</c> (Gemini treats them identically).</para>
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

    // ── Gemini `Schema` dialect → JSON Schema ─────────────────────────────────

    /// <summary>
    /// Restates a Gemini <c>functionDeclarations[].parameters</c> tree as standard JSON
    /// Schema. That field is typed <c>Schema</c> — "Reflects the Open API 3.03 Parameter
    /// Object", rendered as proto3 JSON — so it spells several things in a way no JSON
    /// Schema consumer is defined to understand, and handing it straight to an Anthropic
    /// <c>input_schema</c> or an OpenAI <c>parameters</c> slot ships those spellings
    /// verbatim. Each one is the same statement in the other dialect, so the conversion
    /// is lossless:
    /// <list type="bullet">
    ///   <item><c>type:"STRING"</c> → <c>type:"string"</c>. proto3 JSON renders an enum
    ///         by its name; JSON Schema names its types in lower case.
    ///         <c>TYPE_UNSPECIFIED</c> is dropped, since JSON Schema says
    ///         "unconstrained" by leaving <c>type</c> off.</item>
    ///   <item><c>nullable:true</c> → <c>"null"</c> joined onto whichever of
    ///         <c>type</c>, <c>anyOf</c> and <c>enum</c> is doing the constraining,
    ///         which is how JSON Schema expresses an optional null.</item>
    ///   <item><c>ref</c>/<c>defs</c> → <c>$ref</c>/<c>$defs</c>, pointers included.
    ///         Google models indirection under the unprefixed names because <c>$</c> is
    ///         not a legal proto field name.</item>
    ///   <item><c>example</c> → a one-member <c>examples</c>. JSON Schema keeps its
    ///         samples in an array.</item>
    ///   <item><c>"minItems":"1"</c> → <c>"minItems":1</c>. proto3 JSON quotes an
    ///         <c>int64</c>; JSON Schema counts with numbers.</item>
    /// </list>
    /// <c>propertyOrdering</c> is left as it is: it is a Google extension that their own
    /// <c>parametersJsonSchema</c> documentation uses in JSON Schema mode, and an unknown
    /// keyword is inert everywhere else.
    ///
    /// <para>Only <c>parameters</c> needs this. A declaration that arrived on
    /// <c>parametersJsonSchema</c> is already JSON Schema and must be forwarded
    /// untouched.</para>
    /// </summary>
    public static JsonElement? FromGemini(JsonElement? schema)
    {
        if (schema is not { } s || s.ValueKind != JsonValueKind.Object) return schema;

        var node = JsonNode.Parse(s.GetRawText());
        if (node is null) return schema;
        PromoteGeminiDialect(node);

        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>The <c>Schema.Type</c> enum names, which proto3 JSON spells upper case.</summary>
    private static readonly string[] GeminiTypeNames =
        ["STRING", "NUMBER", "INTEGER", "BOOLEAN", "ARRAY", "OBJECT", "NULL"];

    /// <summary>
    /// The <c>Schema</c> fields typed <c>int64</c>, which proto3 JSON renders as a
    /// <i>string</i> to survive languages without a 64-bit integer.
    /// </summary>
    private static readonly string[] GeminiQuotedIntKeys =
        ["maxItems", "maxLength", "maxProperties", "minItems", "minLength", "minProperties"];

    private static void PromoteGeminiDialect(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) PromoteGeminiDialect(item);
            return;
        }

        if (node is not JsonObject schema) return;

        // Type name first: PromoteGeminiNullable builds a `[type, "null"]` union out of
        // whatever `type` holds, so it has to already be the JSON Schema spelling.
        PromoteGeminiTypeName(schema);
        PromoteGeminiNullable(schema);
        PromoteGeminiReferenceKeywords(schema);
        PromoteGeminiExample(schema);
        PromoteGeminiQuotedInts(schema);

        // `properties` and `$defs` are keyed by the tool author's names, not by JSON
        // Schema keywords, so only their values are subschemas.
        PromoteGeminiSchemaMap(schema["properties"]);
        PromoteGeminiSchemaMap(schema["$defs"]);
        PromoteGeminiDialect(schema["items"]);
        PromoteGeminiDialect(schema["prefixItems"]);
        PromoteGeminiDialect(schema["anyOf"]);
        // A bool `additionalProperties` is a constraint, not a subschema.
        PromoteGeminiDialect(schema["additionalProperties"] as JsonObject);
    }

    private static void PromoteGeminiSchemaMap(JsonNode? node)
    {
        if (node is not JsonObject map) return;
        foreach (var entry in map.ToList()) PromoteGeminiDialect(entry.Value);
    }

    private static void PromoteGeminiTypeName(JsonObject schema)
    {
        if (schema["type"] is not JsonValue value || !value.TryGetValue<string>(out var name)) return;

        if (string.Equals(name, "TYPE_UNSPECIFIED", StringComparison.Ordinal))
        {
            schema.Remove("type");
            return;
        }

        if (Array.IndexOf(GeminiTypeNames, name) >= 0)
            schema["type"] = name.ToLowerInvariant();
    }

    private static void PromoteGeminiNullable(JsonObject schema)
    {
        if (schema["nullable"] is not JsonValue flag) return;

        var nullable = flag.TryGetValue<bool>(out var value) && value;
        schema.Remove("nullable");
        if (!nullable) return;

        if (schema["type"] is JsonValue type && type.TryGetValue<string>(out var name))
            schema["type"] = new JsonArray(JsonValue.Create(name), JsonValue.Create("null"));
        else if (schema["anyOf"] is JsonArray union && !union.Any(IsNullTypeSchema))
            union.Add(new JsonObject { ["type"] = "null" });

        // `enum` is the tighter constraint of the two: widening `type` leaves null
        // unreachable anyway, because the value still has to be one of the members.
        // Google types its enum members as strings and so cannot list null there,
        // which is exactly why the dialect needs a separate flag to say it.
        if (schema["enum"] is JsonArray members && !members.Any(member => member is null))
            members.Add(null);

        // Nothing matched: with no type, union or enum the schema constrains nothing, so
        // null was already admissible and the flag said nothing new. Emitting a bare
        // `type:"null"` here would invert it into "must be null".
    }

    /// <summary>
    /// Recognises a union member that already admits null. The comparison ignores case
    /// because members are converted after their parent, so an incoming tree may still
    /// spell the type <c>NULL</c> at this point.
    /// </summary>
    private static bool IsNullTypeSchema(JsonNode? member)
        => member is JsonObject obj
           && obj["type"] is JsonValue value
           && value.TryGetValue<string>(out var name)
           && string.Equals(name, "null", StringComparison.OrdinalIgnoreCase);

    private static void PromoteGeminiReferenceKeywords(JsonObject schema)
    {
        RenameKey(schema, "ref", "$ref");
        RenameKey(schema, "defs", "$defs");

        if (schema["$ref"] is not JsonValue value || !value.TryGetValue<string>(out var pointer)) return;

        var migrated = pointer.Replace("#/defs/", "#/$defs/", StringComparison.Ordinal);
        if (!string.Equals(migrated, pointer, StringComparison.Ordinal))
            schema["$ref"] = migrated;
    }

    private static void PromoteGeminiExample(JsonObject schema)
    {
        if (schema["example"] is not { } example) return;

        var detached = example.DeepClone();
        schema.Remove("example");

        if (schema["examples"] is null)
            schema["examples"] = new JsonArray(detached);
    }

    /// <summary>
    /// Unquotes the <see cref="GeminiQuotedIntKeys"/>. A value that is not an integer at
    /// all constrains nothing in either dialect and would only give a validator something
    /// to reject, so it goes.
    /// </summary>
    private static void PromoteGeminiQuotedInts(JsonObject schema)
    {
        foreach (var key in GeminiQuotedIntKeys)
        {
            if (schema[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                schema[key] = count;
            else
                schema.Remove(key);
        }
    }

    /// <summary>
    /// Moves <paramref name="from"/> to <paramref name="to"/>, keeping an existing
    /// <paramref name="to"/> when both are present. The value is cloned because a
    /// <see cref="JsonNode"/> cannot be re-parented while it is still attached.
    /// </summary>
    private static void RenameKey(JsonObject obj, string from, string to)
    {
        if (obj[from] is not { } value) return;

        var detached = value.DeepClone();
        obj.Remove(from);

        if (obj[to] is null) obj[to] = detached;
    }

    // ── Reading a caller's declaration ────────────────────────────────────────

    /// <summary>
    /// Reads a tool's parameter schema out of <paramref name="owner"/>, returning
    /// <c>null</c> for "this tool declares no parameters".
    ///
    /// <para>All four protocols document the field as a JSON Schema <i>object</i>
    /// (OpenAI <c>parameters</c>, Anthropic <c>input_schema</c>, Gemini
    /// <c>parameters</c>/<c>parametersJsonSchema</c>), and three of them let it be
    /// left out — OpenAI Chat spells that out: "Omitting <c>parameters</c> defines a
    /// function with an empty parameter list". So an absent field, an explicit JSON
    /// <c>null</c> (which the Responses API allows outright: <c>parameters: unknown |
    /// null</c>) and anything that is not an object all say the same thing, and all
    /// have to arrive in the IR as the same thing. Reading them apart matters because
    /// <see cref="JsonElement"/> reports a JSON <c>null</c> as a <i>present</i>
    /// <see cref="JsonValueKind.Null"/> value, which sails through a plain null check
    /// and reaches the wire as a literal <c>"input_schema": null</c>.</para>
    /// </summary>
    public static JsonElement? ReadSchema(JsonElement owner, string field)
        => owner.TryGetProperty(field, out var schema) && schema.ValueKind == JsonValueKind.Object
            ? schema.Clone()
            : null;

    // Keywords the legacy OpenAPI-subset `parameters` field cannot express. When a
    // schema uses any of these, <see cref="NormaliseForGemini"/> would drop them and
    // collapse referenced subschemas to `{}` — losing type/enum/required. Such
    // schemas must instead go through Gemini's `parametersJsonSchema` field, which
    // accepts full JSON Schema (incl. $ref/$defs) and is replacing the legacy field.
    private static readonly string[] JsonSchemaOnlyKeywords =
        ["$ref", "$defs", "definitions", "allOf", "if", "then", "else", "unevaluatedProperties"];

    /// <summary>
    /// True when <paramref name="schema"/> (anywhere in its tree) uses a keyword that
    /// the legacy Gemini <c>parameters</c> field can't express — meaning it must be
    /// emitted via <c>parametersJsonSchema</c> to avoid silent data loss. The most
    /// common trigger is <c>$ref</c>/<c>$defs</c>, which every schema generator
    /// (Zod, Pydantic, MCP) emits for nested/reused types.
    /// </summary>
    public static bool RequiresJsonSchema(JsonElement? schema)
        => schema is { } s && s.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
           && ContainsJsonSchemaOnlyKeyword(s);

    /// <summary>
    /// True when <paramref name="schema"/> cannot be carried by Gemini's
    /// <c>Schema</c>-typed <c>parameters</c> field and has to be migrated onto
    /// <c>parametersJsonSchema</c> instead — the sibling field Google documents as
    /// taking standard JSON Schema ("Describes the parameters to the function in JSON
    /// Schema format … This field is mutually exclusive with <c>parameters</c>").
    /// Either the schema uses a keyword the OpenAPI-3.0 subset has no place for
    /// (<see cref="RequiresJsonSchema"/>), or it describes an object the subset's
    /// validator refuses to accept (<see cref="DeclaresNoParameters"/>).
    /// </summary>
    public static bool ExceedsGeminiSchemaSubset(JsonElement? schema)
        => RequiresJsonSchema(schema) || DeclaresNoParameters(schema);

    /// <summary>
    /// True when the schema declares an object with no expressible property —
    /// <c>{"type":"object","properties":{}}</c> (every no-arg tool: MCP handoffs,
    /// LangGraph, plain "get current state" calls) or a free-form map declared with
    /// <c>additionalProperties</c> and no <c>properties</c>.
    ///
    /// <para>Gemini's <c>Schema</c>-typed <c>parameters</c> rejects both with
    /// <c>400 INVALID_ARGUMENT "…parameters.properties: should be non-empty for OBJECT
    /// type"</c>, and <c>additionalProperties</c> does not rescue the second shape — the
    /// validator ignores it. That is a limit of <i>that field</i>, not of the API:
    /// <c>parametersJsonSchema</c> reads the same bytes as plain JSON Schema, where an
    /// empty <c>properties</c> map is an ordinary way to say "no arguments". So this
    /// predicate selects a field (see <see cref="ExceedsGeminiSchemaSubset"/>) rather
    /// than discarding the caller's schema — dropping it used to look harmless because
    /// Gemini treats a declaration with no <c>parameters</c> as parameterless anyway,
    /// but the Anthropic-Vertex adapter behind Antigravity PA turns each declaration
    /// into an Anthropic <c>custom</c> tool, where <c>input_schema</c> is required, and
    /// answers a schema-less one with
    /// <c>tools.N.custom.input_schema: Field required</c>.</para>
    /// </summary>
    public static bool DeclaresNoParameters(JsonElement? schema)
    {
        if (schema is not { } s || s.ValueKind != JsonValueKind.Object)
            return false;

        // A reference or union may resolve to real properties elsewhere in the tree;
        // those schemas belong on the parametersJsonSchema path, not here.
        if (ContainsJsonSchemaOnlyKeyword(s)) return false;
        if (s.TryGetProperty("anyOf", out _) || s.TryGetProperty("oneOf", out _)) return false;

        if (s.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && !string.Equals(type.GetString(), "object", StringComparison.OrdinalIgnoreCase))
            return false;

        return !s.TryGetProperty("properties", out var props)
               || props.ValueKind != JsonValueKind.Object
               || !props.EnumerateObject().Any();
    }

    private static bool ContainsJsonSchemaOnlyKeyword(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (Array.IndexOf(JsonSchemaOnlyKeywords, p.Name) >= 0) return true;
                    if (ContainsJsonSchemaOnlyKeyword(p.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (ContainsJsonSchemaOnlyKeyword(item)) return true;
                return false;
            default:
                return false;
        }
    }

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
