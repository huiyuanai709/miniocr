using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniOcr.Models;

/// <summary>Root of MiniOcr config.json (camelCase). Path: see AppConfigStore.</summary>
public sealed class AppConfigFile
{
    public LlmFileConfig? Llm { get; set; }
    public OcrFileConfig? Ocr { get; set; }
    /// <summary>Local HunyuanOCR OpenAI-compatible server (vLLM or llama-server). Used when <c>ocr.mode=hunyuan</c>.</summary>
    public HunyuanFileConfig? Hunyuan { get; set; }
}

public sealed class LlmFileConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.openai.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Max OCR text chars per LLM NER batch (clamped 1000–2_000_000). Default 300000 for long-context models.</summary>
    public int MaxCharsPerRequest { get; set; } = 300000;
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

/// <summary>
/// Local HunyuanOCR server settings. Weights are not shipped with MiniOcr;
/// point <see cref="BaseUrl"/> at vLLM or llama-server.
/// </summary>
public sealed class HunyuanFileConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Server root without a <c>/v1</c> suffix. vLLM default <c>http://127.0.0.1:8000</c>; llama-server often <c>http://127.0.0.1:8080</c>.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
    /// <summary>Optional Bearer token. Empty skips the Authorization header. Local servers often accept <c>EMPTY</c>.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>vLLM: <c>tencent/HunyuanOCR</c>. llama-server <c>--alias</c>: often <c>HYVL</c>.</summary>
    public string Model { get; set; } = "tencent/HunyuanOCR";
    public int TimeoutSeconds { get; set; } = 180;
    /// <summary>Parallel page requests (clamped 1–64). Default 2. Use 1 with llama-server DFlash (<c>--parallel 1</c>).</summary>
    public int Concurrency { get; set; } = 2;
    /// <summary>JPEG quality for page images (clamped 40–95). Default 85.</summary>
    public int JpegQuality { get; set; } = 85;
    /// <summary><c>max_tokens</c> cap sent to the server (clamped 256–16384). Default 8000, matching the official speed harness.</summary>
    public int MaxTokens { get; set; } = 8000;
    /// <summary>Empty uses <see cref="HunyuanRuntimeConfig.DefaultDocumentPrompt"/> (official document-parsing prompt).</summary>
    public string Prompt { get; set; } = "";
}

/// <summary>Resolved HunyuanOCR client settings. <c>apiKey</c> is never logged.</summary>
public sealed class HunyuanRuntimeConfig
{
    /// <summary>Official document-parsing prompt (Tencent HunyuanOCR benchmark / llama.cpp smoke).</summary>
    public const string DefaultDocumentPrompt =
        "提取文档图片中正文的所有信息用markdown格式表示，其中页眉、页脚部分忽略，表格用html格式表达，文档中公式用latex格式表示，按照阅读顺序组织进行解析。";

    /// <summary>Shorter official general-parsing prompt. Fewer output tokens, less layout structure.</summary>
    public const string PlainTextPrompt = "提取图中的文字。";

    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = "http://127.0.0.1:8000";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "tencent/HunyuanOCR";
    public int TimeoutSeconds { get; init; } = 180;
    public int Concurrency { get; init; } = 2;
    public int JpegQuality { get; init; } = 85;
    public int MaxTokens { get; init; } = 8000;
    public string Prompt { get; init; } = DefaultDocumentPrompt;

    public bool IsUsable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Model) &&
        !string.IsNullOrWhiteSpace(Prompt);

    /// <summary>
    /// <c>{baseUrl}/v1/chat/completions</c>. A trailing <c>/v1</c> on the configured root is stripped
    /// so <c>http://127.0.0.1:8080/v1</c> does not become <c>/v1/v1/...</c>.
    /// </summary>
    public static string ChatCompletionsUrl(string baseUrl)
    {
        string root = (baseUrl ?? "").Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^3].TrimEnd('/');
        return root + "/v1/chat/completions";
    }
}

/// <summary>
/// OpenAI chat-completions body for HunyuanOCR. Property names are snake_case
/// (<c>max_tokens</c>, <c>image_url</c>) because vLLM and llama-server expect that wire format.
/// </summary>
public sealed class HunyuanChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<HunyuanChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 8000;
}

public sealed class HunyuanChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<HunyuanContentPart> Content { get; set; } = [];
}

public sealed class HunyuanContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public HunyuanImageUrl? ImageUrl { get; set; }
}

public sealed class HunyuanImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class OcrFileConfig
{
    /// <summary><c>local</c> (Paddle), <c>llm</c> (vision chat), or <c>hunyuan</c> (local HunyuanOCR server). Default local.</summary>
    public string Mode { get; set; } = "local";
    public int? Dpi { get; set; }
    public int? Engines { get; set; }
    public int? LineWorkers { get; set; }
    public int? DetThreads { get; set; }
    public int? RasterWorkers { get; set; }
    public bool? UseCls { get; set; }
    public bool AutoScaleFromCpu { get; set; } = true;
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
