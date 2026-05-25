using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway.Shared.ChatTransit.OpenAi;

// ── Model listing (GET /v1/models) ────────────────────────────────────────────

public class OpenAiModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "model";
    [JsonPropertyName("created")] public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    [JsonPropertyName("owned_by")] public string OwnedBy { get; set; } = "anthropic";
    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("context_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength { get; set; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }
}

public class ModelList
{
    [JsonPropertyName("object")] public string Object { get; set; } = "list";
    [JsonPropertyName("data")] public List<OpenAiModel> Data { get; set; } = [];
}

// ── Chat Completions (POST /v1/chat/completions) ──────────────────────────────

public class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public object? Content { get; set; }
    [JsonPropertyName("name")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("tool_calls")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<object>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ToolCallId { get; set; }
    [JsonExtensionData] public Dictionary<string, object>? ExtensionData { get; set; }
}

public class ToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public JsonElement? Parameters { get; set; }
}

public class Tool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ToolFunction Function { get; set; } = new();
}

public class StreamOptions
{
    [JsonPropertyName("include_usage")] public bool IncludeUsage { get; set; }
}

public class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = [];
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; set; }
    [JsonPropertyName("top_p")] public double? TopP { get; set; }
    [JsonPropertyName("frequency_penalty")] public double? FrequencyPenalty { get; set; }
    [JsonPropertyName("presence_penalty")] public double? PresencePenalty { get; set; }
    [JsonPropertyName("n")] public int? N { get; set; }
    [JsonPropertyName("logprobs")] public bool? Logprobs { get; set; }
    [JsonPropertyName("top_logprobs")] public int? TopLogprobs { get; set; }
    [JsonPropertyName("seed")] public long? Seed { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("tools")] public List<Tool>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public object? ToolChoice { get; set; }
    [JsonPropertyName("parallel_tool_calls")] public bool? ParallelToolCalls { get; set; }
    [JsonPropertyName("stop")] public object? Stop { get; set; }
    [JsonPropertyName("response_format")] public JsonElement? ResponseFormat { get; set; }
    [JsonPropertyName("stream_options")] public StreamOptions? StreamOptions { get; set; }
    [JsonPropertyName("service_tier")] public string? ServiceTier { get; set; }
    [JsonExtensionData] public Dictionary<string, object>? ExtensionData { get; set; }

    public int? EffectiveMaxTokens => MaxCompletionTokens ?? MaxTokens;
}

// ── Responses API (POST /v1/responses) ───────────────────────────────────────

public class ResponsesRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("input")] public object? Input { get; set; }
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("max_output_tokens")] public int? MaxOutputTokens { get; set; }
    [JsonPropertyName("top_p")] public double? TopP { get; set; }
    [JsonPropertyName("tools")] public List<object>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public object? ToolChoice { get; set; }
    [JsonExtensionData] public Dictionary<string, object>? ExtensionData { get; set; }
}

// ── Error responses ───────────────────────────────────────────────────────────

public class OpenAiErrorResponse
{
    [JsonPropertyName("error")] public OpenAiError Error { get; set; } = new();
}

public class OpenAiError
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("code")] public object? Code { get; set; }
}
