using Microsoft.Extensions.AI;

namespace Gateway.Shared.ChatTransit.Mapping;

/// <summary>
/// Maps multimodal content (images, PDFs) between MEAI IR and protocol wire formats.
///   OpenAI: image_url / file (base64 data URI or URL)
///   Anthropic: source.base64 or source.url image blocks
///   Gemini: inlineData / fileData parts
/// </summary>
public static class MultimodalContentMapper
{
    // ── MEAI → Gemini InlineData ──────────────────────────────────────────────

    public record GeminiInlineData(string MimeType, string Base64Data);
    public record GeminiFileData(string MimeType, string FileUri);

    public static GeminiInlineData? ToGeminiInline(DataContent content)
        => content.Data is { IsEmpty: false }
            ? new GeminiInlineData(
                content.MediaType ?? "application/octet-stream",
                Convert.ToBase64String(content.Data.ToArray()))
            : null;

    public static GeminiFileData? ToGeminiFile(UriContent? content)
    {
        if (content is null) return null;
        var uri = content.Uri?.ToString() ?? "";
        if (string.IsNullOrEmpty(uri)) return null;

        var mime = content.MediaType ?? GuessMimeFromUri(uri);
        return new GeminiFileData(mime, uri);
    }

    // ── Anthropic source → MEAI ───────────────────────────────────────────────

    /// <summary>
    /// Parses a {"type":"base64","media_type":"...","data":"..."} Anthropic source object
    /// into a MEAI <see cref="DataContent"/>.
    /// </summary>
    public static DataContent? FromAnthropicBase64Source(string mediaType, string base64Data)
    {
        if (string.IsNullOrEmpty(base64Data)) return null;
        var bytes = Convert.FromBase64String(base64Data);
        return new DataContent(bytes, mediaType);
    }

    /// <summary>
    /// Parses a {"type":"url","url":"..."} Anthropic source object into a MEAI <see cref="UriContent"/>.
    /// </summary>
    public static UriContent? FromAnthropicUrlSource(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return new UriContent(url, GuessMimeFromUri(url));
    }

    // ── OpenAI image_url → MEAI ───────────────────────────────────────────────

    /// <summary>
    /// Converts an OpenAI image_url string (data URI or HTTPS URL) to MEAI content.
    /// data:image/png;base64,... → DataContent
    /// https://...              → UriContent
    /// </summary>
    public static AIContent? FromOpenAiImageUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        if (url.StartsWith("data:", StringComparison.Ordinal))
        {
            // data:[<mediatype>][;base64],<data>
            var commaIdx = url.IndexOf(',');
            if (commaIdx < 0) return null;

            var header = url[5..commaIdx];
            var data = url[(commaIdx + 1)..];
            var mediaType = "image/png";
            var isBase64 = false;

            var parts = header.Split(';');
            if (parts.Length >= 1 && parts[0].Contains('/'))
                mediaType = parts[0];
            if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                isBase64 = true;

            if (!isBase64) return null;
            var bytes = Convert.FromBase64String(data);
            return new DataContent(bytes, mediaType);
        }

        return new UriContent(url, GuessMimeFromUri(url));
    }

    // ── MEAI → Anthropic source ───────────────────────────────────────────────

    public static (string type, string mediaType, string data)? ToAnthropicBase64(DataContent content)
    {
        if (content.Data.IsEmpty) return null;
        return ("base64", content.MediaType ?? "image/png", Convert.ToBase64String(content.Data.ToArray()));
    }

    public static (string type, string url)? ToAnthropicUrl(UriContent content)
    {
        var url = content.Uri?.ToString();
        return string.IsNullOrEmpty(url) ? null : ("url", url);
    }

    // ── MEAI → OpenAI image_url ───────────────────────────────────────────────

    public static string? ToOpenAiImageUrl(AIContent content) => content switch
    {
        DataContent dc when !dc.Data.IsEmpty
            => $"data:{dc.MediaType ?? "image/png"};base64,{Convert.ToBase64String(dc.Data.ToArray())}",
        UriContent uc => uc.Uri?.ToString(),
        _ => null
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GuessMimeFromUri(string uri)
    {
        var lower = uri.ToLowerInvariant();
        if (lower.Contains(".png")) return "image/png";
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
        if (lower.Contains(".gif")) return "image/gif";
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".pdf")) return "application/pdf";
        return "image/jpeg";
    }
}
