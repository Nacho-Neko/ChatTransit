using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway.Shared.ChatTransit.Anthropic;

// ── Request models ────────────────────────────────────────────────────────────

public class AnthropicMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public object? Content { get; set; }
}

public class AnthropicTool
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; set; }
}

public class AnthropicThinking
{
    [JsonPropertyName("type")] public string Type { get; set; } = "enabled";
    [JsonPropertyName("budget_tokens")] public int? BudgetTokens { get; set; }
}

public class AnthropicMetadata
{
    [JsonPropertyName("user_id")] public string? UserId { get; set; }
}

public class AnthropicMessagesRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<AnthropicMessage> Messages { get; set; } = [];
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 4096;
    [JsonPropertyName("system")] public object? System { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("top_p")] public double? TopP { get; set; }
    [JsonPropertyName("top_k")] public int? TopK { get; set; }
    [JsonPropertyName("tools")] public List<AnthropicTool>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public object? ToolChoice { get; set; }
    [JsonPropertyName("stop_sequences")] public List<string>? StopSequences { get; set; }
    [JsonPropertyName("thinking")] public AnthropicThinking? Thinking { get; set; }
    [JsonPropertyName("metadata")] public AnthropicMetadata? Metadata { get; set; }
    [JsonExtensionData] public Dictionary<string, object>? ExtensionData { get; set; }
}

// ── Response models ───────────────────────────────────────────────────────────

public class AnthropicUsage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("cache_creation_input_tokens")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CacheCreationInputTokens { get; set; }
    [JsonPropertyName("cache_read_input_tokens")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CacheReadInputTokens { get; set; }
}

public class AnthropicMessagesResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = $"msg_{Guid.NewGuid():N}"[..28];
    [JsonPropertyName("type")] public string Type { get; set; } = "message";
    [JsonPropertyName("role")] public string Role { get; set; } = "assistant";
    [JsonPropertyName("content")] public List<object> Content { get; set; } = [];
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("usage")] public AnthropicUsage Usage { get; set; } = new();
}

// ── Model listing ─────────────────────────────────────────────────────────────

public class AnthropicModel
{
    [JsonPropertyName("type")] public string Type { get; set; } = "model";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "2024-01-01T00:00:00Z";

    [JsonPropertyName("context_window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextWindow { get; set; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }
}

public class AnthropicModelListResponse
{
    [JsonPropertyName("data")] public List<AnthropicModel> Data { get; set; } = [];
    [JsonPropertyName("has_more")] public bool HasMore { get; set; } = false;
    [JsonPropertyName("first_id")] public string? FirstId { get; set; }
    [JsonPropertyName("last_id")] public string? LastId { get; set; }
}

// ── Error models ──────────────────────────────────────────────────────────────

public class AnthropicErrorDetail
{
    [JsonPropertyName("type")] public string Type { get; set; } = "error";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public class AnthropicErrorResponse
{
    [JsonPropertyName("type")] public string Type { get; set; } = "error";
    [JsonPropertyName("error")] public AnthropicErrorDetail Error { get; set; } = new();
}
