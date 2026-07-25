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
