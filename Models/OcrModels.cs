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

    /// <summary>
    /// Local PDF path for debug (for example a Desktop file with a non-ASCII name).
    /// Used when <see cref="Files"/> and <see cref="Url"/> are empty.
    /// </summary>
    public string? Path { get; set; }
}

/// <summary>
/// Debug body for <c>POST /ocr?verbose=1</c> and <c>POST /ocr/upload?verbose=1</c>.
/// <see cref="Pages"/> matches the competition result: pages with no rules are omitted.
/// </summary>
public sealed class OcrTextDebugResponse
{
    public bool Ok { get; set; }
    public string Mode { get; set; } = "";
    public string Source { get; set; } = "";
    public int Dpi { get; set; }
    public int PageCount { get; set; }
    public double MsPerPage { get; set; }
    public OcrTimings Timings { get; set; } = new();
    public string TextLayerMode { get; set; } = "auto";
    public int TextLayerPages { get; set; }
    public int OcrPages { get; set; }
    public List<OcrTextDebugPage> Pages { get; set; } = [];
}

public sealed class OcrTextDebugPage
{
    public int Page { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double RasterizeMs { get; set; }
    public double OcrMs { get; set; }
    public string Text { get; set; } = "";
    /// <summary><c>textLayer</c> when the PDF text was used, otherwise <c>ocr</c>.</summary>
    public string Source { get; set; } = "ocr";
    /// <summary>Contest rules for this page. Present only when the list has items.</summary>
    public List<ChallengeRule>? RuleList { get; set; }
}

public sealed class OcrPageResult
{
    public int Page { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Text { get; set; } = "";
    public double RasterizeMs { get; set; }
    public double OcrMs { get; set; }
    /// <summary><c>textLayer</c> when the PDF text was used, otherwise <c>ocr</c>.</summary>
    public string Source { get; set; } = "ocr";
    /// <summary>Optional prebuilt contest rules (vision OCR). When set, mapper prefers these.</summary>
    public List<ChallengeRule>? RuleList { get; set; }
}

public sealed class OcrTimings
{
    public double DownloadMs { get; set; }
    public double RasterizeMs { get; set; }
    public double OcrMs { get; set; }
    public double TotalMs { get; set; }

    /// <summary>Text-layer classification wall time. Zero when the text layer is off.</summary>
    public double AnalyzeMs { get; set; }

    /// <summary>
    /// Local LLM NER wall span: first request start through the last response.
    /// Requests overlap OCR, so this can be larger than the piece of <see cref="TotalMs"/>
    /// that is still waiting on NER. On a distributed cluster job this is the tail after
    /// the last OCR page until NER finishes, and <see cref="NerRequestMs"/> stays 0
    /// (those requests run on the workers).
    /// </summary>
    public double NerMs { get; set; }

    /// <summary>
    /// Sum of local LLM NER request durations. Concurrent requests add up, so this can
    /// exceed <see cref="NerMs"/>. Zero when this process did not call the LLM.
    /// </summary>
    public double NerRequestMs { get; set; }

    public int NerGroups { get; set; }

    /// <summary>Most local NER requests in flight at once.</summary>
    public int NerPeak { get; set; }

    /// <summary>Time spent in vkQueueSubmit during this job, summed across engines.</summary>
    public double GpuSubmitMs { get; set; }
    /// <summary>Time spent blocked on the GPU fence during this job.</summary>
    public double GpuWaitMs { get; set; }
    /// <summary>CPU upload and command-buffer record during this job.</summary>
    public double CpuPreMs { get; set; }
    /// <summary>CPU readback and CTC decode while the GPU result is in hand.</summary>
    public double CpuPostMs { get; set; }
    /// <summary>Pages whose detection and recognition both stayed on the GPU.</summary>
    public int GpuPages { get; set; }
    /// <summary>Pages where at least one session fell back to CPU.</summary>
    public int FallbackPages { get; set; }
    /// <summary>VK_ERROR_DEVICE_LOST count for this process, including startup.</summary>
    public int DeviceLost { get; set; }
    public string GpuDevice { get; set; } = "";
    /// <summary>One-time device creation plus startup warmup. Repeated on every job line.</summary>
    public double InitMs { get; set; }
    /// <summary><c>win32</c>, <c>syncfd</c>, or <c>blocking</c> (vkWaitForFences).</summary>
    public string Fence { get; set; } = "";
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
    public string TextLayerMode { get; set; } = "auto";
    public int TextLayerPageCount { get; set; }
    public int OcrPageCount { get; set; }
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
    /// <summary>When true, high-saturation red is removed from color pages before detection.</summary>
    public bool RemoveRedSeal { get; set; } = true;
    public int RasterWorkerCount { get; set; }
    /// <summary><c>parallel</c> (default) or <c>inprocess</c>.</summary>
    public string RenderMode { get; set; } = "parallel";
    /// <summary>PDFtoImage.Parallel worker processes. Unused while <see cref="RenderMode"/> is <c>inprocess</c>.</summary>
    public int RenderProcessCount { get; set; }
    /// <summary><c>auto</c>, <c>off</c>, or <c>force</c>.</summary>
    public string TextLayer { get; set; } = "auto";
    public int RecBatchLines { get; set; }
    /// <summary>Backend that will actually run: <c>cpu</c>, <c>vulkan</c>, or <c>metal</c>. A <c>metal</c> request off macOS is reported as <c>cpu</c>.</summary>
    public string OcrBackend { get; set; } = "cpu";
    /// <summary>Recognizer intra-op threads on each pooled engine. <c>0</c> is the library default.</summary>
    public int RecIntraOpThreads { get; set; } = 1;
    /// <summary>Selected Vulkan device, empty when the process is not using Vulkan.</summary>
    public string VulkanDevice { get; set; } = "";
    /// <summary>Device-local heap of <see cref="VulkanDevice"/>, in bytes.</summary>
    public ulong VulkanDeviceLocalBytes { get; set; }
    /// <summary>Vulkan or Metal device name. Empty on CPU.</summary>
    public string GpuDevice { get; set; } = "";
    /// <summary>Vulkan device-local bytes, or Metal recommendedMaxWorkingSetSize.</summary>
    public ulong GpuMemoryBytes { get; set; }
    public int DetLimitSideLength { get; set; }
    public bool AutoScaleFromCpu { get; set; }
    public string ConfigPath { get; set; } = "";
    public bool ConfigFileExisted { get; set; }
    public string ConfigPathSource { get; set; } = "";
    public string OcrMode { get; set; } = "local";
    public string WeChatKind { get; set; } = "";
    public string WeChatPluginPath { get; set; } = "";
    public string WeChatDir { get; set; } = "";
    public int WeChatInstances { get; set; }
    /// <summary><c>off</c>, <c>ready</c>, or a short fallback reason.</summary>
    public string WeChatStatus { get; set; } = "off";
    public int LlmOcrConcurrency { get; set; }
    public bool LlmEnabled { get; set; }
    public bool LlmUsable { get; set; }
    public string LlmModel { get; set; } = "";
    public string LlmBaseUrl { get; set; } = "";
    public bool LlmFallbackToHeuristics { get; set; }
    /// <summary>Never the raw key — only "(set)" or "(empty)".</summary>
    public string LlmApiKey { get; set; } = "(empty)";

    /// <summary>Present only when <c>cluster.enabled</c> is true. Omitted otherwise.</summary>
    public ClusterHealthInfo? Cluster { get; set; }
}
