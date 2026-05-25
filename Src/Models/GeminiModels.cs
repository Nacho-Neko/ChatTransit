using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway.Shared.ChatTransit.Gemini;

// ── Request: POST /v1beta/models/{model}:generateContent ─────────────────────

public class GenerateContentRequest
{
    [JsonPropertyName("contents")] public List<GeminiContent> Contents { get; set; } = [];
    [JsonPropertyName("systemInstruction")] public GeminiContent? SystemInstruction { get; set; }
    [JsonPropertyName("generationConfig")] public GenerationConfig? GenerationConfig { get; set; }
    [JsonPropertyName("tools")] public List<GeminiToolSet>? Tools { get; set; }
    [JsonPropertyName("toolConfig")] public ToolConfig? ToolConfig { get; set; }
    [JsonPropertyName("safetySettings")] public List<SafetySetting>? SafetySettings { get; set; }
    [JsonPropertyName("cachedContent")] public string? CachedContent { get; set; }
    [JsonExtensionData] public Dictionary<string, object>? ExtensionData { get; set; }
}

public class GeminiContent
{
    [JsonPropertyName("role")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Role { get; set; }
    [JsonPropertyName("parts")] public List<GeminiPart> Parts { get; set; } = [];
}

public class GeminiPart
{
    [JsonPropertyName("text")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
    [JsonPropertyName("inlineData")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public InlineData? InlineData { get; set; }
    [JsonPropertyName("fileData")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FileData? FileData { get; set; }
    [JsonPropertyName("functionCall")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FunctionCall? FunctionCall { get; set; }
    [JsonPropertyName("functionResponse")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FunctionResponse? FunctionResponse { get; set; }
    [JsonPropertyName("thought")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Thought { get; set; }
    [JsonPropertyName("thoughtSignature")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ThoughtSignature { get; set; }
}

public class InlineData
{
    [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

public class FileData
{
    [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "";
    [JsonPropertyName("fileUri")] public string FileUri { get; set; } = "";
}

public class FunctionCall
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("args")] public JsonElement? Args { get; set; }
    [JsonPropertyName("id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Id { get; set; }
}

public class FunctionResponse
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("response")] public JsonElement? ResponseData { get; set; }
    [JsonPropertyName("id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Id { get; set; }
}

public class GenerationConfig
{
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("topP")] public double? TopP { get; set; }
    [JsonPropertyName("topK")] public int? TopK { get; set; }
    [JsonPropertyName("maxOutputTokens")] public int? MaxOutputTokens { get; set; }
    [JsonPropertyName("stopSequences")] public List<string>? StopSequences { get; set; }
    [JsonPropertyName("responseMimeType")] public string? ResponseMimeType { get; set; }
    [JsonPropertyName("responseSchema")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? ResponseSchema { get; set; }
    [JsonPropertyName("candidateCount")] public int? CandidateCount { get; set; }
    [JsonPropertyName("presencePenalty")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PresencePenalty { get; set; }
    [JsonPropertyName("frequencyPenalty")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FrequencyPenalty { get; set; }
    [JsonPropertyName("seed")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Seed { get; set; }
    [JsonPropertyName("thinkingConfig")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ThinkingConfig? ThinkingConfig { get; set; }
}

public class ThinkingConfig
{
    [JsonPropertyName("thinkingBudget")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ThinkingBudget { get; set; }
}

public class ToolConfig
{
    [JsonPropertyName("functionCallingConfig")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FunctionCallingConfig? FunctionCallingConfig { get; set; }
}

public class FunctionCallingConfig
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "AUTO";
    [JsonPropertyName("allowedFunctionNames")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? AllowedFunctionNames { get; set; }
}

public class GeminiToolSet
{
    [JsonPropertyName("functionDeclarations")] public List<FunctionDeclaration>? FunctionDeclarations { get; set; }
}

public class FunctionDeclaration
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public JsonElement? Parameters { get; set; }
}

public class SafetySetting
{
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("threshold")] public string Threshold { get; set; } = "";
}

// ── Response ─────────────────────────────────────────────────────────────────

public class GenerateContentResponse
{
    [JsonPropertyName("candidates")] public List<Candidate> Candidates { get; set; } = [];
    [JsonPropertyName("usageMetadata")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public UsageMetadata? UsageMetadata { get; set; }
    [JsonPropertyName("promptFeedback")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PromptFeedback? PromptFeedback { get; set; }
    [JsonPropertyName("modelVersion")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ModelVersion { get; set; }
}

public class Candidate
{
    [JsonPropertyName("content")] public GeminiContent Content { get; set; } = new();
    [JsonPropertyName("finishReason")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FinishReason { get; set; }
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("safetyRatings")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<SafetyRating>? SafetyRatings { get; set; }
    [JsonPropertyName("citationMetadata")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CitationMetadata? CitationMetadata { get; set; }
}

public class SafetyRating
{
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("probability")] public string Probability { get; set; } = "NEGLIGIBLE";
}

public class CitationMetadata
{
    [JsonPropertyName("citationSources")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<CitationSource>? CitationSources { get; set; }
}

public class CitationSource
{
    [JsonPropertyName("startIndex")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int StartIndex { get; set; }
    [JsonPropertyName("endIndex")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int EndIndex { get; set; }
    [JsonPropertyName("uri")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Uri { get; set; }
    [JsonPropertyName("license")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? License { get; set; }
}

public class PromptFeedback
{
    [JsonPropertyName("blockReason")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BlockReason { get; set; }
    [JsonPropertyName("safetyRatings")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<SafetyRating>? SafetyRatings { get; set; }
}

public class UsageMetadata
{
    [JsonPropertyName("promptTokenCount")] public int PromptTokenCount { get; set; }
    [JsonPropertyName("candidatesTokenCount")] public int CandidatesTokenCount { get; set; }
    [JsonPropertyName("totalTokenCount")] public int TotalTokenCount { get; set; }
    [JsonPropertyName("cachedContentTokenCount")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CachedContentTokenCount { get; set; }
}

// ── Model info (GET /v1beta/models) ──────────────────────────────────────────

public class ListModelsResponse
{
    [JsonPropertyName("models")] public List<ModelInfo> Models { get; set; } = [];
    [JsonPropertyName("nextPageToken")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? NextPageToken { get; set; }
}

public class ModelInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("inputTokenLimit")] public int InputTokenLimit { get; set; }
    [JsonPropertyName("outputTokenLimit")] public int OutputTokenLimit { get; set; }
    [JsonPropertyName("supportedGenerationMethods")] public List<string> SupportedGenerationMethods { get; set; } = [];
}

// ── Google model listing response (used by CommonController /v1/models) ──────

public class GoogleModel
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "001";
    [JsonPropertyName("supportedGenerationMethods")]
    public List<string> SupportedGenerationMethods { get; set; } = ["generateContent", "countTokens"];

    [JsonPropertyName("inputTokenLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InputTokenLimit { get; set; }

    [JsonPropertyName("outputTokenLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputTokenLimit { get; set; }
}

public class GoogleModelListResponse
{
    [JsonPropertyName("models")] public List<GoogleModel> Models { get; set; } = [];
}

// ── Error ─────────────────────────────────────────────────────────────────────

public class GeminiErrorResponse
{
    [JsonPropertyName("error")] public GeminiErrorDetail Error { get; set; } = new();
}

public class GeminiErrorDetail
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "INTERNAL";
}
