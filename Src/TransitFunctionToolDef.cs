using System.Text.Json;

namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Protocol-neutral function tool definition.
/// Stored in <see cref="TransitRequest.FunctionTools"/> and re-encoded
/// in target-protocol format by each <see cref="Abstractions.IRequestEncoder"/>.
/// </summary>
public sealed class TransitFunctionToolDef
{
    /// <summary>Function name (snake_case recommended).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description shown to the model.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// JSON Schema describing the function parameters.
    /// May be transformed (e.g. Gemini-stripped) by <see cref="Mapping.FunctionSchemaMapper"/>.
    /// </summary>
    public JsonElement? ParametersSchema { get; init; }
}
