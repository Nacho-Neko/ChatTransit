using ChatTransit.Inbound;
using ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// Function/response schemas that use $ref/$defs (every Zod/Pydantic/MCP generator
/// emits them) must survive OpenAI → Gemini. The legacy OpenAPI-subset
/// `parameters` field can't express references and the sanitizer would collapse
/// them to {}, so such schemas are routed through `parametersJsonSchema`, which
/// accepts full JSON Schema.
/// </summary>
public class GeminiSchemaJsonSchemaTests
{
    private static JsonElement EncodeToolDecl(string model, string schema)
    {
        var json = """
        {"model":"__MODEL__","messages":[{"role":"user","content":"go"}],
         "tools":[{"type":"function","function":{"name":"f","parameters":__SCHEMA__}}]}
        """.Replace("__MODEL__", model).Replace("__SCHEMA__", schema);
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var root = JsonDocument.Parse(new GeminiOutboundEncoder().Encode(transit)).RootElement;
        return root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
    }

    [Fact]
    public void RefDefs_Schema_Routed_Through_ParametersJsonSchema()
    {
        const string schema = """
        {"type":"object","$defs":{"P":{"type":"string","enum":["low","high"]}},
         "properties":{"priority":{"$ref":"#/$defs/P"}},"required":["priority"]}
        """;
        var decl = EncodeToolDecl("gemini-3-pro", schema);

        // Preserved verbatim under parametersJsonSchema, NOT collapsed under parameters.
        decl.TryGetProperty("parameters", out _).Should().BeFalse();
        var pjs = decl.GetProperty("parametersJsonSchema");
        pjs.GetProperty("$defs").GetProperty("P").GetProperty("enum")[0].GetString().Should().Be("low");
        pjs.GetProperty("properties").GetProperty("priority").GetProperty("$ref").GetString()
            .Should().Be("#/$defs/P");
    }

    /// <summary>
    /// A schema the `Schema`-typed `parameters` field cannot hold is a reason to switch
    /// fields, not to discard the caller's declaration. `parameters` 400s on an OBJECT
    /// with empty `properties` ("should be non-empty for OBJECT type") and ignores
    /// `additionalProperties`; `parametersJsonSchema` reads the very same bytes as JSON
    /// Schema, where both say exactly what they mean.
    ///
    /// <para>Dropping the schema instead used to look free, because Gemini reads a
    /// declaration with no `parameters` as parameterless anyway. It is not free one hop
    /// further on: the Anthropic-Vertex adapter behind Antigravity PA turns each
    /// declaration into an Anthropic `custom` tool, whose `input_schema` is required, and
    /// answers a schema-less one with `tools.N.custom.input_schema: Field required`.</para>
    /// </summary>
    [Theory]
    // No-arg tool as every OpenAI-shape client emits it.
    [InlineData("""{"type":"object","properties":{}}""")]
    [InlineData("""{"type":"object","properties":{},"required":[]}""")]
    // Free-form map: Gemini's `parameters` validator ignores additionalProperties.
    [InlineData("""{"type":"object","description":"kv pairs","additionalProperties":true}""")]
    public void Parameterless_Object_Schema_Migrates_To_ParametersJsonSchema(string schema)
    {
        var decl = EncodeToolDecl("gemini-3-pro", schema);

        decl.TryGetProperty("parameters", out _).Should().BeFalse();
        decl.GetProperty("name").GetString().Should().Be("f");

        var pjs = decl.GetProperty("parametersJsonSchema");
        pjs.GetProperty("type").GetString().Should().Be("object");
        // Verbatim: the caller's own wording survives, so `additionalProperties` still
        // declares the free-form map and `description` still reaches the model.
        JsonSerializer.Deserialize<JsonElement>(schema).EnumerateObject()
            .Should().AllSatisfy(expected =>
                pjs.GetProperty(expected.Name).GetRawText().Should().Be(expected.Value.GetRawText()));
    }

    /// <summary>
    /// Both Gemini parameter fields are optional and Google documents the empty case on
    /// the legacy one — "For function with no parameters, this can be left unset" — so a
    /// caller that declared nothing must not have a schema invented for it either.
    /// </summary>
    [Fact]
    public void Tool_Declaring_No_Schema_At_All_Emits_Neither_Field()
    {
        const string json = """
        {"model":"gemini-3-pro","messages":[{"role":"user","content":"go"}],
         "tools":[{"type":"function","function":{"name":"ping","description":"pong"}}]}
        """;
        var transit = new OpenAiChatInboundDecoder().Decode(Encoding.UTF8.GetBytes(json));
        var root = JsonDocument.Parse(new GeminiOutboundEncoder().Encode(transit)).RootElement;
        var decl = root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];

        decl.GetProperty("name").GetString().Should().Be("ping");
        decl.TryGetProperty("parameters", out _).Should().BeFalse();
        decl.TryGetProperty("parametersJsonSchema", out _).Should().BeFalse();
    }

    [Fact]
    public void Plain_Schema_Still_Uses_Legacy_Parameters_Field()
    {
        const string schema = """
        {"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}
        """;
        var decl = EncodeToolDecl("gemini-3-pro", schema);

        decl.TryGetProperty("parametersJsonSchema", out _).Should().BeFalse();
        decl.GetProperty("parameters").GetProperty("properties").GetProperty("city")
            .GetProperty("type").GetString().Should().Be("string");
    }
}
