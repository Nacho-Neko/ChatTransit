using ChatTransit.Inbound;
using ChatTransit.Outbound;
using System.Text;
using System.Text.Json;

namespace ChatTransit.Tests.Roundtrip;

/// <summary>
/// A tool's parameter schema is the one part of a request where the four protocols
/// disagree about the <i>field</i> rather than the contents, so every pair needs the
/// caller's declaration restated in the target's own terms instead of filtered, dropped
/// or invented:
///
/// <list type="bullet">
///   <item>OpenAI Chat — <c>parameters?: unknown</c>. Optional, and absence is the
///         documented spelling of the empty case: "Omitting <c>parameters</c> defines a
///         function with an empty parameter list."</item>
///   <item>OpenAI Responses — <c>parameters: unknown | null</c>. Required, but nullable.</item>
///   <item>Anthropic — <c>input_schema</c> required, root validated as the constant
///         <c>"object"</c>, <c>properties</c> optional.</item>
///   <item>Gemini — <c>parameters</c> (proto <c>Schema</c>, OpenAPI-3.0 subset) and
///         <c>parametersJsonSchema</c> (standard JSON Schema), both optional and mutually
///         exclusive. "For function with no parameters, this can be left unset."</item>
/// </list>
///
/// <para>The failure these guard against is a schema that is legal on the way in and
/// missing on the way out. It surfaces a hop later than the transcode — Antigravity PA
/// re-encodes a Gemini body as Anthropic <c>custom</c> tools, so a declaration that lost
/// its schema comes back as
/// <c>400 tools.0.custom.input_schema: Field required</c>.</para>
/// </summary>
public class ToolSchemaProtocolContractTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static byte[] OpenAiChatRequest(string toolJson) => Encoding.UTF8.GetBytes($$"""
        {"model":"m","messages":[{"role":"user","content":"go"}],"tools":[{{toolJson}}]}
        """);

    private static byte[] AnthropicRequest(string toolJson) => Encoding.UTF8.GetBytes($$"""
        {"model":"claude-opus-4-6","max_tokens":64,
         "messages":[{"role":"user","content":"go"}],"tools":[{{toolJson}}]}
        """);

    private static byte[] GeminiRequest(string declarationJson) => Encoding.UTF8.GetBytes($$"""
        {"contents":[{"role":"user","parts":[{"text":"go"}]}],
         "tools":[{"functionDeclarations":[{{declarationJson}}]}]}
        """);

    private static JsonElement Parse(byte[] utf8Json)
        => JsonDocument.Parse(utf8Json).RootElement.Clone();

    private static JsonElement GeminiDeclaration(TransitRequest transit)
        => Parse(new GeminiOutboundEncoder().Encode(transit))
            .GetProperty("tools")[0].GetProperty("functionDeclarations")[0];

    private static JsonElement AnthropicTool(TransitRequest transit)
        => Parse(new AnthropicOutboundEncoder().Encode(transit)).GetProperty("tools")[0];

    private static JsonElement OpenAiChatFunction(TransitRequest transit)
        => Parse(new OpenAiChatOutboundEncoder().Encode(transit))
            .GetProperty("tools")[0].GetProperty("function");

    private static JsonElement OpenAiResponsesTool(TransitRequest transit)
        => Parse(new OpenAiResponsesOutboundEncoder().Encode(transit)).GetProperty("tools")[0];

    // ── A declared no-arg schema survives to every target ─────────────────────

    /// <summary>
    /// <c>{"type":"object","properties":{}}</c> is what every OpenAI-shape client emits for
    /// a no-arg tool (MCP handoffs, LangGraph, plain "get current state" calls). It is a
    /// complete JSON Schema, so no target may answer it with nothing.
    /// </summary>
    [Fact]
    public void Declared_NoArg_Schema_Reaches_Anthropic_As_Object_InputSchema()
    {
        var transit = new OpenAiChatInboundDecoder().Decode(OpenAiChatRequest("""
            {"type":"function","function":{"name":"list_calendars",
             "parameters":{"type":"object","properties":{}}}}
            """));

        var schema = AnthropicTool(transit).GetProperty("input_schema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void Declared_NoArg_Schema_Reaches_Gemini_As_ParametersJsonSchema()
    {
        var transit = new OpenAiChatInboundDecoder().Decode(OpenAiChatRequest("""
            {"type":"function","function":{"name":"list_calendars",
             "parameters":{"type":"object","properties":{}}}}
            """));

        var decl = GeminiDeclaration(transit);
        // NOT the `Schema`-typed field: an OBJECT there with empty `properties` is a hard
        // 400 ("parameters.properties: should be non-empty for OBJECT type").
        decl.TryGetProperty("parameters", out _).Should().BeFalse();
        decl.GetProperty("parametersJsonSchema").GetProperty("type").GetString().Should().Be("object");
    }

    // ── "No schema declared" is spelled per target, never invented ─────────────

    /// <summary>
    /// The one case where a schema legitimately has to be produced: Anthropic requires the
    /// field, so "no arguments" has to be said out loud. The minimal legal value says it —
    /// <c>properties</c> is optional there, so nothing beyond the root is fabricated.
    /// </summary>
    [Fact]
    public void Undeclared_Schema_Becomes_The_Minimal_Object_For_Anthropic()
    {
        var transit = new OpenAiChatInboundDecoder().Decode(OpenAiChatRequest("""
            {"type":"function","function":{"name":"ping"}}
            """));

        transit.FunctionTools![0].ParametersSchema.Should().BeNull("the caller declared none");

        var schema = AnthropicTool(transit).GetProperty("input_schema");
        schema.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void Undeclared_Schema_Stays_Absent_On_OpenAi_Chat()
    {
        var transit = new AnthropicInboundDecoder().Decode(AnthropicRequest("""
            {"name":"ping","description":"pong"}
            """));

        // Optional field, and absence IS the documented empty-parameter-list spelling.
        OpenAiChatFunction(transit).TryGetProperty("parameters", out _).Should().BeFalse();
    }

    [Fact]
    public void Undeclared_Schema_Becomes_Explicit_Null_On_OpenAi_Responses()
    {
        var transit = new AnthropicInboundDecoder().Decode(AnthropicRequest("""
            {"name":"ping","description":"pong"}
            """));

        // Required but nullable, so the field is present and null — not omitted, and not
        // padded with a schema the caller never wrote.
        var tool = OpenAiResponsesTool(transit);
        tool.TryGetProperty("parameters", out var parameters).Should().BeTrue();
        parameters.ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// An explicit JSON <c>null</c> says the same thing as an absent field, but
    /// <see cref="JsonElement"/> reports it as a <i>present</i>
    /// <see cref="JsonValueKind.Null"/>, so a plain null check lets it through and the
    /// wire ends up carrying a literal <c>"input_schema": null</c>.
    /// </summary>
    [Fact]
    public void Explicit_Null_Schema_Is_Read_As_No_Declaration()
    {
        var transit = new AnthropicInboundDecoder().Decode(AnthropicRequest("""
            {"name":"ping","input_schema":null}
            """));

        transit.FunctionTools![0].ParametersSchema.Should().BeNull();
        AnthropicTool(transit).GetProperty("input_schema").GetProperty("type")
            .GetString().Should().Be("object");
    }

    // ── Gemini → JSON Schema targets ──────────────────────────────────────────

    /// <summary>
    /// The decoder used to read <c>parameters</c> only, so a caller on the newer field —
    /// gemini-cli, anything that ran a <c>parametersJsonSchema</c> promotion pass, and this
    /// library's own Gemini encoder — silently lost its entire schema.
    /// </summary>
    [Fact]
    public void Gemini_ParametersJsonSchema_Is_Read_And_Forwarded_Verbatim()
    {
        var transit = new GeminiInboundDecoder().Decode(GeminiRequest("""
            {"name":"search","parametersJsonSchema":{"type":"object",
             "$defs":{"P":{"type":"string","enum":["low","high"]}},
             "properties":{"priority":{"$ref":"#/$defs/P"}},"required":["priority"]}}
            """));

        var schema = AnthropicTool(transit).GetProperty("input_schema");
        schema.GetProperty("properties").GetProperty("priority").GetProperty("$ref")
            .GetString().Should().Be("#/$defs/P");
        schema.GetProperty("$defs").GetProperty("P").GetProperty("enum")[0]
            .GetString().Should().Be("low");
    }

    /// <summary>
    /// The <c>Schema</c>-typed <c>parameters</c> is proto3 JSON, so it spells types, nulls,
    /// references, samples and int64s in ways a JSON Schema consumer has no definition for.
    /// Each is the same statement in the other dialect.
    /// </summary>
    [Fact]
    public void Gemini_Legacy_Parameters_Dialect_Is_Converted_For_JsonSchema_Targets()
    {
        var transit = new GeminiInboundDecoder().Decode(GeminiRequest("""
            {"name":"search","parameters":{"type":"OBJECT","properties":{
               "city":{"type":"STRING","example":"berlin"},
               "note":{"type":"STRING","nullable":true},
               "tags":{"type":"ARRAY","items":{"type":"STRING"},"minItems":"1"},
               "kind":{"ref":"#/defs/K"}},
             "defs":{"K":{"type":"STRING"}},"required":["city"]}}
            """));

        var schema = AnthropicTool(transit).GetProperty("input_schema");
        var props = schema.GetProperty("properties");

        schema.GetProperty("type").GetString().Should().Be("object");
        props.GetProperty("city").GetProperty("type").GetString().Should().Be("string");
        // proto3 JSON keeps one sample in `example`; JSON Schema keeps an array.
        props.GetProperty("city").GetProperty("examples")[0].GetString().Should().Be("berlin");
        // `nullable` has no JSON Schema counterpart — a type union is how it is said.
        props.GetProperty("note").GetProperty("type").EnumerateArray()
            .Select(t => t.GetString()).Should().Equal("string", "null");
        // int64 fields render as strings over proto3 JSON; JSON Schema counts with numbers.
        props.GetProperty("tags").GetProperty("minItems").GetInt32().Should().Be(1);
        // `$` is not a legal proto field name, so Google models indirection unprefixed.
        props.GetProperty("kind").GetProperty("$ref").GetString().Should().Be("#/$defs/K");
        schema.GetProperty("$defs").GetProperty("K").GetProperty("type")
            .GetString().Should().Be("string");
    }

    /// <summary>
    /// proto3 JSON names the field <c>function_declarations</c> while every published
    /// example camel-cases it, and Google accepts both. A pass keyed on one spelling reads
    /// the other as a built-in retriever and finds no tools at all.
    /// </summary>
    [Fact]
    public void Gemini_Snake_Cased_Declarations_Are_Read_As_Functions()
    {
        var transit = new GeminiInboundDecoder().Decode(Encoding.UTF8.GetBytes("""
            {"contents":[{"role":"user","parts":[{"text":"go"}]}],
             "tools":[{"function_declarations":[{"name":"search",
               "parameters_json_schema":{"type":"object","properties":{"q":{"type":"string"}}}}]}]}
            """));

        transit.FunctionTools.Should().NotBeNull().And.HaveCount(1);
        transit.FunctionTools![0].Name.Should().Be("search");
        AnthropicTool(transit).GetProperty("input_schema").GetProperty("properties")
            .GetProperty("q").GetProperty("type").GetString().Should().Be("string");
    }

    /// <summary>
    /// A Gemini caller that left both fields unset declared no arguments; the fact has to
    /// reach the IR as such so each target can spell it its own way.
    /// </summary>
    [Fact]
    public void Gemini_Declaration_Without_Either_Field_Declares_No_Parameters()
    {
        var transit = new GeminiInboundDecoder().Decode(GeminiRequest("""
            {"name":"ping","description":"pong"}
            """));

        transit.FunctionTools![0].ParametersSchema.Should().BeNull();
        OpenAiChatFunction(transit).TryGetProperty("parameters", out _).Should().BeFalse();
        AnthropicTool(transit).GetProperty("input_schema").GetProperty("type")
            .GetString().Should().Be("object");
    }
}
