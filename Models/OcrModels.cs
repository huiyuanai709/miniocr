namespace MiniOcr.Models;

/// <summary>
/// Debug sync <c>POST /ocr</c> body. Prefer competition fields; legacy <see cref="Url"/> still maps to
/// <c>files:[{fileId:"f1",url}]</c>. DPI may also come from query <c>?dpi=</c> or server config.
/// </summary>
public sealed class OcrDebugRequest
{
    public int TeamId { get; set; }
    public string? Key { get; set; }
    /// <summary>Ignored for sync debug (no callback); accepted for competition-shaped bodies.</summary>
    public string? CallbackUrl { get; set; }
    public List<ChallengeFileRef>? Files { get; set; }

    /// <summary>Legacy single-URL debug field; mapped to fileId <c>f1</c> when <see cref="Files"/> is empty.</summary>
    public string? Url { get; set; }

    /// <summary>Optional per-request raster DPI override (36–300). Defaults to query/env/config.</summary>
    public int? Dpi { get; set; }
}

public sealed class OcrPageResult
{
    public int Page { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Text { get; set; } = "";
    public double RasterizeMs { get; set; }
    public double OcrMs { get; set; }
    /// <summary>Optional prebuilt contest rules (vision OCR). When set, mapper prefers these.</summary>
    public List<ChallengeRule>? RuleList { get; set; }
}

public sealed class OcrTimings
{
    public double DownloadMs { get; set; }
    public double RasterizeMs { get; set; }
    public double OcrMs { get; set; }
    public double TotalMs { get; set; }
}

public sealed class EntityHit
{
    public string Name { get; set; } = "";
    public List<int> Pages { get; set; } = [];
    public int Count { get; set; }
}

public sealed class OcrEntities
{
    public List<EntityHit> Companies { get; set; } = [];
    public List<EntityHit> Persons { get; set; } = [];
}

/// <summary>Internal OCR pipeline result (not the competition callback shape).</summary>
public sealed class OcrResponse
{
    public bool Ok { get; set; }
    public int PageCount { get; set; }
    public long PdfBytes { get; set; }
    public string DownloadMode { get; set; } = "";
    public int Dpi { get; set; }
    public OcrTimings Timings { get; set; } = new();
    public List<OcrPageResult> Pages { get; set; } = [];
    public OcrEntities? Entities { get; set; }
    public string? Error { get; set; }
}

public sealed class HealthResponse
{
    public string Status { get; set; } = "ok";
    public bool ModelsLoaded { get; set; }
    public string Runtime { get; set; } = "";
    public int ProcessorCount { get; set; }
    public int EngineCount { get; set; }
    public int LineWorkerCount { get; set; }
    public int DetIntraOpThreads { get; set; }
    public int DefaultDpi { get; set; }
    public bool UseDirectionClassification { get; set; }
    public int RasterWorkerCount { get; set; }
    public int RecBatchLines { get; set; }
    public int DetLimitSideLength { get; set; }
    public bool AutoScaleFromCpu { get; set; }
    public string ConfigPath { get; set; } = "";
    public bool ConfigFileExisted { get; set; }
    public string ConfigPathSource { get; set; } = "";
    public string OcrMode { get; set; } = "local";
    public int LlmOcrConcurrency { get; set; }
    public bool LlmEnabled { get; set; }
    public bool LlmUsable { get; set; }
    public string LlmModel { get; set; } = "";
    public string LlmBaseUrl { get; set; } = "";
    public bool LlmFallbackToHeuristics { get; set; }
    /// <summary>Never the raw key — only "(set)" or "(empty)".</summary>
    public string LlmApiKey { get; set; } = "(empty)";
    public bool HunyuanUsable { get; set; }
    public string HunyuanModel { get; set; } = "";
    public string HunyuanBaseUrl { get; set; } = "";
    public int HunyuanConcurrency { get; set; }
    /// <summary>Never the raw key — only "(set)" or "(empty)".</summary>
    public string HunyuanApiKey { get; set; } = "(empty)";
}
