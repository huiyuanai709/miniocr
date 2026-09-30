using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniOcr.Models;

/// <summary>Root of MiniOcr config.json (camelCase). Path: see AppConfigStore.</summary>
public sealed class AppConfigFile
{
    public LlmFileConfig? Llm { get; set; }
    public OcrFileConfig? Ocr { get; set; }
    /// <summary>Optional multi-machine OCR. Missing or <c>enabled: false</c> keeps single-node behavior.</summary>
    public ClusterFileConfig? Cluster { get; set; }
    /// <summary>Optional Nacos service discovery. Missing or <c>enabled: false</c> keeps static worker discovery.</summary>
    public NacosFileConfig? Nacos { get; set; }
}

public sealed class LlmFileConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.openai.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Safety cap on OCR text chars per LLM NER request (clamped 1000–2_000_000). A pages-per-request group that would exceed this is split. Default 300000.</summary>
    public int MaxCharsPerRequest { get; set; } = 300000;
    /// <summary>Non-empty OCR pages per LLM NER request (clamped 1–2000). Default 10. Blank pages are skipped and do not count.</summary>
    public int PagesPerRequest { get; set; } = 10;
    /// <summary>Max parallel LLM NER batch completions (clamped 1–32). Default 8.</summary>
    public int MaxConcurrency { get; set; } = 8;
    /// <summary>Max parallel vision OCR page calls when ocr.mode=llm (clamped 1–256).</summary>
    public int OcrConcurrency { get; set; } = 32;
    /// <summary>Hint for max chars of page text the vision model should return.</summary>
    public int OcrMaxCharsHint { get; set; } = 8000;
    /// <summary>JPEG encode quality for vision OCR pages (clamped 40–95). Default 70.</summary>
    public int OcrJpegQuality { get; set; } = 70;
    /// <summary>
    /// DeepSeek-style thinking mode (bool or "enabled"/"disabled").
    /// Default false → API sends {"type":"disabled"}. deepseek-flash/v4 enables thinking by default;
    /// NER/OCR should disable for speed.
    /// </summary>
    [JsonConverter(typeof(ThinkingConfigJsonConverter))]
    public bool Thinking { get; set; } = false;

    /// <summary>When LLM is not usable: use EntityExtractor heuristics. Ignored after an LLM NER attempt (never silent heuristic fallback). Default false.</summary>
    public bool FallbackToHeuristics { get; set; } = false;
}

public sealed class OcrFileConfig
{
    /// <summary><c>local</c> (Paddle), <c>llm</c> (vision), or <c>wechat</c> (Windows x64 plugin). Default local.</summary>
    public string Mode { get; set; } = "local";
    public int? Dpi { get; set; }
    public int? Engines { get; set; }
    public int? LineWorkers { get; set; }
    public int? DetThreads { get; set; }
    public int? RasterWorkers { get; set; }
    /// <summary><c>inprocess</c> (default) or <c>parallel</c> (PDFtoImage.Parallel worker processes).</summary>
    public string RenderMode { get; set; } = "inprocess";
    /// <summary>Worker processes when <see cref="RenderMode"/> is <c>parallel</c>. Null = auto from cores and engine count.</summary>
    public int? RenderProcesses { get; set; }
    public bool? UseCls { get; set; }
    public bool AutoScaleFromCpu { get; set; } = true;

    /// <summary>Full path to WeChatOCR.exe (3.9) or wxocr.dll (4.x). Empty = auto-detect.</summary>
    public string? WeChatOcrPath { get; set; }
    /// <summary>WeChat version directory containing mmmojo_64.dll. Empty = auto-detect.</summary>
    public string? WeChatDir { get; set; }
    /// <summary>WeChatOCR processes. Null = auto (about ProcessorCount/4, max 3).</summary>
    public int? WeChatInstances { get; set; }
    /// <summary>If the plugin is missing or this is not Windows x64, start local Paddle instead. Default true.</summary>
    public bool WeChatFallbackToLocal { get; set; } = true;
    public int? WeChatConnectTimeoutSeconds { get; set; }
    public int? WeChatRequestTimeoutSeconds { get; set; }
}

/// <summary>Resolved LLM settings after file + env overrides (apiKey never logged).</summary>
public sealed class LlmRuntimeConfig
{
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "https://api.openai.com";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxCharsPerRequest { get; init; } = 300000;
    /// <summary>Non-empty pages sent to one NER request. Default 10.</summary>
    public int PagesPerRequest { get; init; } = 10;
    public int MaxConcurrency { get; init; } = 8;
    public int OcrConcurrency { get; init; } = 32;
    public int OcrMaxCharsHint { get; init; } = 8000;
    /// <summary>JPEG quality for vision page images (40–95). Default 70.</summary>
    public int OcrJpegQuality { get; init; } = 70;
    /// <summary>When true, send thinking.type=enabled; when false (default), send disabled.</summary>
    public bool Thinking { get; init; } = false;
    public bool FallbackToHeuristics { get; init; } = false;

    /// <summary>Payload for DeepSeek/OpenAI-compatible thinking field.</summary>
    public ThinkingOption ToThinkingOption() =>
        new() { Type = Thinking ? "enabled" : "disabled" };

    public bool IsUsable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Model);
}

/// <summary>OpenAI chat completions request (AOT source-gen) — string message content (NER).</summary>
public sealed class ChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
    public double Temperature { get; set; }
    /// <summary>DeepSeek thinking control: {"type":"enabled"|"disabled"}.</summary>
    public ThinkingOption? Thinking { get; set; }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>OpenAI-compatible multimodal chat completions (vision OCR).</summary>
public sealed class VisionChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<VisionChatMessage> Messages { get; set; } = [];
    public double Temperature { get; set; }
    /// <summary>DeepSeek thinking control: {"type":"enabled"|"disabled"}.</summary>
    public ThinkingOption? Thinking { get; set; }
}

/// <summary>OpenAI/DeepSeek thinking object serialized as camelCase <c>thinking: { type }</c>.</summary>
public sealed class ThinkingOption
{
    public string Type { get; set; } = "disabled";
}

/// <summary>
/// Accepts JSON bool, 0/1 number, or string enabled/disabled/true/false/1/0 for <c>llm.thinking</c>.
/// </summary>
public sealed class ThinkingConfigJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => false,
            JsonTokenType.Number => reader.TryGetInt64(out long n) ? n != 0 : false,
            JsonTokenType.String => ParseThinkingString(reader.GetString()),
            _ => throw new JsonException($"Unexpected token for llm.thinking: {reader.TokenType}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);

    public static bool ParseThinkingString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string s = raw.Trim();
        if (s is "1" or "true" or "True" or "TRUE" or "yes" or "YES" or "on" or "ON" or "enabled" or "Enabled" or "ENABLED")
            return true;
        if (s is "0" or "false" or "False" or "FALSE" or "no" or "NO" or "off" or "OFF" or "disabled" or "Disabled" or "DISABLED")
            return false;
        return false;
    }
}

public sealed class VisionChatMessage
{
    public string Role { get; set; } = "";
    public List<VisionContentPart> Content { get; set; } = [];
}

public sealed class VisionContentPart
{
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
    public VisionImageUrl? ImageUrl { get; set; }
}

public sealed class VisionImageUrl
{
    public string Url { get; set; } = "";
}

public sealed class ChatCompletionResponse
{
    public List<ChatChoice>? Choices { get; set; }
}

public sealed class ChatChoice
{
    public ChatMessage? Message { get; set; }
}

/// <summary>config.json <c>nacos</c> section for service discovery.</summary>
public sealed class NacosFileConfig
{
    public bool Enabled { get; set; }
    /// <summary>Nacos server address, e.g. "http://127.0.0.1:8848".</summary>
    public string ServerAddr { get; set; } = "";
    /// <summary>Namespace id. Empty = public namespace.</summary>
    public string Namespace { get; set; } = "";
    /// <summary>Service name to register under and discover from.</summary>
    public string ServiceName { get; set; } = "miniocr-cluster";
    /// <summary>Group name. Default "DEFAULT_GROUP".</summary>
    public string GroupName { get; set; } = "DEFAULT_GROUP";
    /// <summary>Cluster name for Nacos's cluster concept. Default "DEFAULT".</summary>
    public string ClusterName { get; set; } = "DEFAULT";
    /// <summary>Weight for this instance (1.0 = full). Default 1.0.</summary>
    public double Weight { get; set; } = 1.0;
    /// <summary>Access token for Nacos auth (optional).</summary>
    public string AccessToken { get; set; } = "";
    /// <summary>Username for Nacos auth (optional).</summary>
    public string Username { get; set; } = "";
    /// <summary>Password for Nacos auth (optional).</summary>
    public string Password { get; set; } = "";
    /// <summary>Health check interval in seconds for heartbeat. Default 5.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    /// <summary>Healthy check threshold. Default 1.2x heartbeat interval.</summary>
    public double HealthyCheckSeconds { get; set; } = 6.0;
    /// <summary>Map Nacos instance metadata to this node's metadata.</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    public bool HasAuth() =>
        !string.IsNullOrWhiteSpace(AccessToken) ||
        (!string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password));
}

/// <summary>Strict LLM NER payload: {"companies":["..."],"persons":["..."]}.</summary>
public sealed class LlmEntityPayload
{
    public List<string>? Companies { get; set; }
    public List<string>? Persons { get; set; }
}

/// <summary>
/// Vision OCR page payload. Prefer contest-shaped <see cref="RuleList"/>;
/// <see cref="Text"/> is full-page OCR text used for origin snippets / fallback mapping.
/// </summary>
public sealed class LlmVisionOcrPayload
{
    public string? Text { get; set; }
    public List<ChallengeRule>? RuleList { get; set; }
    public List<string>? Companies { get; set; }
    public List<string>? Persons { get; set; }
}
