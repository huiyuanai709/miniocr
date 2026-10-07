using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Throughput knobs from AppData config.json + environment overrides.
/// Env (MINIOCR_*) wins over file for OCR. Auto-scales from ProcessorCount when
/// <c>autoScaleFromCpu</c> is true and engines/workers are unset.
/// </summary>
public sealed class OcrRuntimeConfig
{
    /// <summary><c>local</c> (Paddle), <c>llm</c> (vision), or <c>wechat</c> (Windows x64 plugin). Default local.</summary>
    public string Mode { get; init; } = "local";
    public int EngineCount { get; init; }
    public int DefaultDpi { get; init; }
    public int LineWorkerCount { get; init; }
    public int DetIntraOpThreads { get; init; }
    public bool UseDirectionClassification { get; init; }
    public int RasterWorkerCount { get; init; }
    /// <summary><c>parallel</c> (default) or <c>inprocess</c>.</summary>
    public string RenderMode { get; init; } = "parallel";
    /// <summary>Worker processes used when <see cref="RenderMode"/> is <c>parallel</c>.</summary>
    public int RenderProcessCount { get; init; } = 1;
    /// <summary><c>auto</c> (default), <c>off</c>, or <c>force</c>.</summary>
    public string TextLayer { get; init; } = "auto";
    public int TextLayerMinChars { get; init; } = 40;
    public double TextLayerMaxUnknownRatio { get; init; } = 0.02;
    public double TextLayerImageCoverage { get; init; } = 0.55;
    public int TextLayerImageMinChars { get; init; } = 200;
    public int RecBatchLines { get; init; }
    /// <summary><c>cpu</c> (default), <c>auto</c>, or <c>vulkan</c>.</summary>
    public string Backend { get; init; } = "cpu";
    /// <summary>Recognizer intra-op threads per engine. <c>0</c> lets the library choose.</summary>
    public int RecIntraOpThreads { get; init; } = 1;
    /// <summary>Vulkan device index or name substring. Empty picks the first discrete GPU.</summary>
    public string VulkanDevice { get; init; } = "";
    public int DetLimitSideLength { get; init; }
    public bool AutoScaleFromCpu { get; init; }
    public int ProcessorCount { get; init; }

    /// <summary>Explicit WeChatOCR.exe or wxocr.dll. Empty = auto-detect.</summary>
    public string? WeChatOcrPath { get; init; }
    /// <summary>WeChat version directory that contains mmmojo_64.dll. Empty = auto-detect.</summary>
    public string? WeChatDir { get; init; }
    /// <summary>How many WeChatOCR processes to keep. Each one runs a single request at a time.</summary>
    public int WeChatInstances { get; init; } = 1;
    /// <summary>When the plugin is missing or this is not Windows x64, fall back to local Paddle.</summary>
    public bool WeChatFallbackToLocal { get; init; } = true;
    public int WeChatConnectTimeoutSeconds { get; init; } = 20;
    public int WeChatRequestTimeoutSeconds { get; init; } = 60;

    /// <summary>Load using env only (legacy / tests).</summary>
    public static OcrRuntimeConfig FromEnvironment() =>
        FromAppConfig(new AppConfigFile());

    /// <summary>
    /// Merge file OCR section with env overrides and CPU auto-scale.
    /// Priority: env MINIOCR_* &gt; explicit file values &gt; auto-scale defaults.
    /// </summary>
    public static OcrRuntimeConfig FromAppConfig(AppConfigFile file)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        OcrFileConfig ocr = file.Ocr ?? new OcrFileConfig();
        bool autoScale = ocr.AutoScaleFromCpu;
        string mode = ResolveMode(ocr.Mode);

        AutoScaleDefaults scaled = ComputeAutoScale(cores);

        int engines = ResolveInt(
            envName: "MINIOCR_ENGINES",
            fileValue: ocr.Engines,
            autoDefault: scaled.Engines,
            autoScale: autoScale,
            fixedFallback: scaled.Engines);
        engines = Math.Clamp(engines, 1, 16);

        // Recompute line/det defaults against the *resolved* engine count.
        // Detection and recognition do not overlap inside one Run, so det is
        // sized to fill the cores on its own (see ComputeWorkersForEngines).
        AutoScaleDefaults forEngines = ComputeWorkersForEngines(cores, engines);

        int line = ResolveInt(
            "MINIOCR_LINE_WORKERS",
            ocr.LineWorkers,
            forEngines.LineWorkers,
            autoScale,
            forEngines.LineWorkers);
        line = Math.Clamp(line, 1, 16);

        int det = ResolveInt(
            "MINIOCR_DET_THREADS",
            ocr.DetThreads,
            forEngines.DetThreads,
            autoScale,
            forEngines.DetThreads);
        det = Math.Clamp(det, 1, 16);

        // local default 96; llm default 72 when dpi unset (vision is IO-bound; fewer pixels = faster encode/upload).
        // Explicit ocr.dpi or MINIOCR_DPI always wins — do not change local's 96 when mode=local.
        int dpiFallback = ocr.Dpi ?? (string.Equals(mode, "llm", StringComparison.OrdinalIgnoreCase) ? 72 : 96);
        int dpi = Math.Clamp(ReadInt("MINIOCR_DPI", dpiFallback), 36, 300);

        bool useClsFile = ocr.UseCls ?? false;
        bool useCls = ReadBool("MINIOCR_USE_CLS", useClsFile);

        // Vision OCR is IO-bound: leave CPU free for PDFium — default raster workers = min(8, cores).
        bool llmMode = string.Equals(mode, "llm", StringComparison.OrdinalIgnoreCase);
        int rasterDefault;
        if (llmMode)
            rasterDefault = Math.Clamp(Math.Min(8, cores), 1, 8);
        else if (autoScale)
            rasterDefault = forEngines.RasterWorkers;
        else
            rasterDefault = Math.Clamp(Math.Min(engines, 4), 1, 8);
        int raster = ResolveInt(
            "MINIOCR_RASTER_WORKERS",
            ocr.RasterWorkers,
            rasterDefault,
            autoScale || llmMode,
            rasterDefault);
        raster = Math.Clamp(raster, 1, 8);

        string renderMode = ResolveRenderMode(ocr.RenderMode);
        int renderAuto = ComputeRenderProcesses(cores, engines);
        int renderProcesses = ResolveInt(
            "MINIOCR_RENDER_PROCESSES",
            ocr.RenderProcesses,
            renderAuto,
            autoScale,
            fixedFallback: renderAuto);
        renderProcesses = Math.Clamp(renderProcesses, 1, 8);

        string textLayer = ResolveTextLayer(ocr.TextLayer);
        int textMinChars = Math.Clamp(
            ResolveInt("MINIOCR_TEXT_LAYER_MIN_CHARS", ocr.TextLayerMinChars, 40, autoScale: true, fixedFallback: 40),
            1, 100_000);
        double textUnknown = Math.Clamp(
            ResolveDouble("MINIOCR_TEXT_LAYER_MAX_UNKNOWN_RATIO", ocr.TextLayerMaxUnknownRatio, 0.02),
            0, 1);
        double textImage = Math.Clamp(
            ResolveDouble("MINIOCR_TEXT_LAYER_IMAGE_COVERAGE", ocr.TextLayerImageCoverage, 0.55),
            0, 1);
        int textImageChars = Math.Clamp(
            ResolveInt("MINIOCR_TEXT_LAYER_IMAGE_MIN_CHARS", ocr.TextLayerImageMinChars, 200, autoScale: true, fixedFallback: 200),
            1, 100_000);

        int detLimit = Math.Clamp(ReadInt("MINIOCR_DET_LIMIT_SIDE", 960), 64, 4096);
        // 1 keeps a pool of engines from each claiming the whole CPU. 0 is the library's auto budget.
        int recIntra = Math.Clamp(
            ReadInt("MINIOCR_REC_INTRA_OP_THREADS", ocr.RecIntraOpThreads ?? 1),
            0,
            16);
        string backend = CanonicalBackend(FirstSet(
            Environment.GetEnvironmentVariable("MINIOCR_OCR_BACKEND"),
            ocr.Backend));
        // A GPU graph does not use a CPU intra-op team, and recognition is one
        // queued submission. Leave the explicit file/env values alone.
        bool gpuBudget = BackendForHost(backend) is "vulkan" or "metal";
        ApplyGpuThreadBudget(
            gpuBudget,
            lineExplicit: Environment.GetEnvironmentVariable("MINIOCR_LINE_WORKERS") is not null || ocr.LineWorkers is not null,
            detExplicit: Environment.GetEnvironmentVariable("MINIOCR_DET_THREADS") is not null || ocr.DetThreads is not null,
            ref line,
            ref det);
        int recBatch = ResolveRecBatch(gpuBudget);
        string vulkanDevice = FirstSet(
            Environment.GetEnvironmentVariable("MINIOCR_OCR_VULKAN_DEVICE"),
            ocr.VulkanDevice) ?? "";

        // One WeChatOCR process is single-threaded. Default is a few processes, not one per core:
        // a 12-thread laptop gets 3. Cap the auto default at 3; explicit values may go up to 8.
        int wechatAuto = Math.Clamp(Math.Max(1, cores / 4), 1, 3);
        int wechatInstances = ResolveInt(
            "MINIOCR_WECHAT_INSTANCES",
            ocr.WeChatInstances,
            wechatAuto,
            autoScale,
            fixedFallback: 1);
        wechatInstances = Math.Clamp(wechatInstances, 1, 8);

        int connectTimeout = ResolveInt(
            "MINIOCR_WECHAT_CONNECT_TIMEOUT",
            ocr.WeChatConnectTimeoutSeconds,
            autoDefault: 20,
            autoScale: true,
            fixedFallback: 20);
        int requestTimeout = ResolveInt(
            "MINIOCR_WECHAT_REQUEST_TIMEOUT",
            ocr.WeChatRequestTimeoutSeconds,
            autoDefault: 60,
            autoScale: true,
            fixedFallback: 60);

        bool wechatFallback = ocr.WeChatFallbackToLocal;
        string? fallbackEnv = Environment.GetEnvironmentVariable("MINIOCR_WECHAT_FALLBACK");
        if (!string.IsNullOrWhiteSpace(fallbackEnv))
            wechatFallback = ReadBool("MINIOCR_WECHAT_FALLBACK", wechatFallback);

        return new OcrRuntimeConfig
        {
            Mode = mode,
            EngineCount = engines,
            DefaultDpi = dpi,
            LineWorkerCount = line,
            DetIntraOpThreads = det,
            UseDirectionClassification = useCls,
            RasterWorkerCount = raster,
            RenderMode = renderMode,
            RenderProcessCount = renderProcesses,
            TextLayer = textLayer,
            TextLayerMinChars = textMinChars,
            TextLayerMaxUnknownRatio = textUnknown,
            TextLayerImageCoverage = textImage,
            TextLayerImageMinChars = textImageChars,
            RecBatchLines = recBatch,
            Backend = backend,
            RecIntraOpThreads = recIntra,
            VulkanDevice = vulkanDevice,
            DetLimitSideLength = detLimit,
            AutoScaleFromCpu = autoScale,
            ProcessorCount = cores,
            WeChatOcrPath = FirstSet(Environment.GetEnvironmentVariable("MINIOCR_WECHAT_OCR_PATH"), ocr.WeChatOcrPath),
            WeChatDir = FirstSet(Environment.GetEnvironmentVariable("MINIOCR_WECHAT_DIR"), ocr.WeChatDir),
            WeChatInstances = wechatInstances,
            WeChatFallbackToLocal = wechatFallback,
            WeChatConnectTimeoutSeconds = Math.Clamp(connectTimeout, 3, 180),
            WeChatRequestTimeoutSeconds = Math.Clamp(requestTimeout, 5, 300),
        };
    }

    /// <summary>
    /// New curve (2–64 cores): engines = Clamp(cores/2, 1, min(16, cores)).
    /// Old awkward default forced min 4 engines even on 2-core boxes.
    /// Line workers stay on the old 1.0–1.5× shared budget (typically 2).
    /// Detection runs before recognition, so det threads are
    /// Clamp(ceil(cores/engines), 1, 8) and are not subtracted from that budget.
    /// raster = Clamp(min(engines, cores/2), 1, 8).
    /// </summary>
    public static AutoScaleDefaults ComputeAutoScale(int cores)
    {
        cores = Math.Max(1, cores);
        int engines = Math.Clamp(cores / 2, 1, Math.Min(16, cores));
        return ComputeWorkersForEngines(cores, engines) with { Engines = engines };
    }

    public static AutoScaleDefaults ComputeWorkersForEngines(int cores, int engines)
    {
        cores = Math.Max(1, cores);
        engines = Math.Clamp(engines, 1, 16);

        // Line-worker budget ≈ 1.25× cores (band 1.0–1.5×), counted with a
        // placeholder det share. Real detection threads are assigned below.
        int target = Math.Max(engines * 2, (int)Math.Round(cores * 1.25));
        int maxBudget = Math.Max(engines * 2, (int)Math.Floor(cores * 1.5));
        target = Math.Min(target, maxBudget);

        int perEngine = Math.Max(2, (target + engines - 1) / engines);
        // Prefer slightly more line workers than the placeholder det share.
        int line = Math.Clamp((perEngine + 1) / 2, 1, 8);
        int det = Math.Clamp(perEngine - line, 1, 8);

        // Shrink line workers if the old shared budget is still oversubscribed.
        // The det value in this loop is only that placeholder; the real detection
        // thread count is assigned after, because DET and REC do not run together.
        while (engines * (line + det) > maxBudget && (line > 1 || det > 1))
        {
            if (line >= det && line > 1)
                line--;
            else if (det > 1)
                det--;
            else
                break;
        }

        det = Math.Clamp((cores + engines - 1) / engines, 1, 8);

        int raster = Math.Clamp(Math.Min(engines, Math.Max(1, cores / 2)), 1, 8);

        return new AutoScaleDefaults
        {
            Engines = engines,
            LineWorkers = line,
            DetThreads = det,
            RasterWorkers = raster,
        };
    }

    private static int ResolveInt(
        string envName,
        int? fileValue,
        int autoDefault,
        bool autoScale,
        int fixedFallback)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (int.TryParse(raw, out int fromEnv))
            return fromEnv;
        if (fileValue is int fv)
            return fv;
        return autoScale ? autoDefault : fixedFallback;
    }

    /// <summary>
    /// Copy with an explicit mode. Does not re-read <c>MINIOCR_OCR_MODE</c>, so a
    /// startup fallback to local stays local even when the env var requested wechat or llm.
    /// </summary>
    public OcrRuntimeConfig WithMode(string mode) => With(mode: CanonicalMode(mode));

    /// <summary>
    /// Copy with an explicit render mode. Does not re-read <c>MINIOCR_RENDER_MODE</c>,
    /// so a startup fallback to in-process stays in-process even when the env var requested parallel.
    /// </summary>
    public OcrRuntimeConfig WithRenderMode(string renderMode) =>
        With(renderMode: CanonicalRenderMode(renderMode));

    public OcrRuntimeConfig With(string? mode = null, int? engineCount = null, int? wechatInstances = null, string? renderMode = null) => new()
    {
        Mode = mode is null ? Mode : CanonicalMode(mode),
        EngineCount = engineCount ?? EngineCount,
        DefaultDpi = DefaultDpi,
        LineWorkerCount = LineWorkerCount,
        DetIntraOpThreads = DetIntraOpThreads,
        UseDirectionClassification = UseDirectionClassification,
        RasterWorkerCount = RasterWorkerCount,
        RenderMode = renderMode is null ? RenderMode : CanonicalRenderMode(renderMode),
        RenderProcessCount = RenderProcessCount,
        TextLayer = TextLayer,
        TextLayerMinChars = TextLayerMinChars,
        TextLayerMaxUnknownRatio = TextLayerMaxUnknownRatio,
        TextLayerImageCoverage = TextLayerImageCoverage,
        TextLayerImageMinChars = TextLayerImageMinChars,
        RecBatchLines = RecBatchLines,
        Backend = Backend,
        RecIntraOpThreads = RecIntraOpThreads,
        VulkanDevice = VulkanDevice,
        DetLimitSideLength = DetLimitSideLength,
        AutoScaleFromCpu = AutoScaleFromCpu,
        ProcessorCount = ProcessorCount,
        WeChatOcrPath = WeChatOcrPath,
        WeChatDir = WeChatDir,
        WeChatInstances = wechatInstances ?? WeChatInstances,
        WeChatFallbackToLocal = WeChatFallbackToLocal,
        WeChatConnectTimeoutSeconds = WeChatConnectTimeoutSeconds,
        WeChatRequestTimeoutSeconds = WeChatRequestTimeoutSeconds,
    };

    public bool IsParallelRender =>
        string.Equals(RenderMode, "parallel", StringComparison.OrdinalIgnoreCase);

    public bool TextLayerEnabled =>
        !string.Equals(TextLayer, "off", StringComparison.OrdinalIgnoreCase);

    public bool IsLlmMode =>
        string.Equals(Mode, "llm", StringComparison.OrdinalIgnoreCase);

    public bool IsWeChatMode =>
        string.Equals(Mode, "wechat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Env MINIOCR_OCR_MODE overrides file. Accepts local|llm|wechat (case-insensitive).
    /// Unknown values fall back to local.
    /// </summary>
    public static string ResolveMode(string? fileMode)
    {
        string? env = Environment.GetEnvironmentVariable("MINIOCR_OCR_MODE");
        string raw = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (fileMode ?? "local");
        return CanonicalMode(raw);
    }

    /// <summary>
    /// Env MINIOCR_RENDER_MODE overrides file. Accepts inprocess|parallel (case-insensitive).
    /// Unknown values fall back to parallel.
    /// </summary>
    public static string ResolveRenderMode(string? fileMode)
    {
        string? env = Environment.GetEnvironmentVariable("MINIOCR_RENDER_MODE");
        string raw = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (fileMode ?? "parallel");
        return CanonicalRenderMode(raw);
    }

    public static string CanonicalRenderMode(string? raw)
    {
        if (string.Equals(raw, "inprocess", StringComparison.OrdinalIgnoreCase))
            return "inprocess";
        return "parallel";
    }

    /// <summary>
    /// Env MINIOCR_OCR_TEXT_LAYER overrides file. Accepts auto|off|force (case-insensitive).
    /// Unknown values fall back to auto.
    /// </summary>
    public static string ResolveTextLayer(string? fileMode)
    {
        string? env = Environment.GetEnvironmentVariable("MINIOCR_OCR_TEXT_LAYER");
        string raw = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (fileMode ?? "auto");
        return CanonicalTextLayer(raw);
    }

    /// <summary>
    /// <c>cpu</c> (default), <c>auto</c>, <c>vulkan</c>, or <c>metal</c>. Anything else stays on CPU
    /// so an unknown value cannot switch the text to a GPU fp16 path.
    /// </summary>
    public static string CanonicalBackend(string? raw)
    {
        if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
            return "auto";
        if (string.Equals(raw, "vulkan", StringComparison.OrdinalIgnoreCase))
            return "vulkan";
        if (string.Equals(raw, "metal", StringComparison.OrdinalIgnoreCase))
            return "metal";
        return "cpu";
    }

    /// <summary>
    /// <c>metal</c> runs only on macOS. Other hosts keep the name in config
    /// (<see cref="CanonicalBackend"/>) but the engine uses CPU.
    /// </summary>
    public static string BackendForHost(string backend) =>
        backend == "metal" && !OperatingSystem.IsMacOS() ? "cpu" : backend;

    public static string CanonicalTextLayer(string? raw)
    {
        if (string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
            return "off";
        if (string.Equals(raw, "force", StringComparison.OrdinalIgnoreCase))
            return "force";
        return "auto";
    }

    private static double ResolveDouble(string envName, double? fileValue, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fromEnv))
            return fromEnv;
        return fileValue ?? fallback;
    }

    /// <summary>
    /// Auto worker-process count for parallel render. The fork's render benchmark
    /// knees around 4 processes; leave the remaining cores for OCR engines.
    /// </summary>
    /// <summary>
    /// Device-local heaps under 4 GB keep a single Vulkan engine. Two engines
    /// each keep a detection arena and a recognition arena, which does not fit
    /// a 2 GB part such as the MX450 once the desktop is using the card.
    /// </summary>
    /// <summary>
    /// Device memory under 4 GB runs one engine. Pass the Vulkan device-local
    /// heap, or Metal <c>recommendedMaxWorkingSetSize</c>.
    /// </summary>
    /// <summary>
    /// GPU auto budget: detection preprocess and line workers stay at 2 unless
    /// the operator set them. The CPU curve is unchanged.
    /// </summary>
    public static void ApplyGpuThreadBudget(bool gpu, bool lineExplicit, bool detExplicit, ref int line, ref int det)
    {
        if (!gpu) return;
        if (!lineExplicit) line = Math.Clamp(Math.Min(line, 2), 1, 16);
        if (!detExplicit) det = Math.Clamp(Math.Min(det, 2), 1, 16);
    }

    /// <summary>
    /// Recognizer lines per GPU submission. CPU stays at 8. <c>MINIOCR_REC_BATCH</c> wins.
    /// Same-width lines stay exact-width, so a larger batch does not pad logits.
    /// </summary>
    public static int ResolveRecBatch(bool gpu)
    {
        string? raw = Environment.GetEnvironmentVariable("MINIOCR_REC_BATCH");
        if (int.TryParse(raw, out int fromEnv))
            return Math.Clamp(fromEnv, 1, 64);
        return gpu ? 32 : 8;
    }

    public static int VulkanEngineCount(ulong deviceLocalBytes, int requested)
    {
        requested = Math.Clamp(requested, 1, 16);
        const ulong fourGiB = 4UL << 30;
        if (deviceLocalBytes > 0 && deviceLocalBytes < fourGiB)
            return 1;
        return requested;
    }

    public static int ComputeRenderProcesses(int cores, int engines)
    {
        cores = Math.Max(1, cores);
        engines = Math.Clamp(engines, 1, 16);
        return Math.Clamp(Math.Min(4, Math.Max(1, cores - engines)), 1, 4);
    }

    public static string CanonicalMode(string? raw)
    {
        if (string.Equals(raw, "llm", StringComparison.OrdinalIgnoreCase))
            return "llm";
        if (string.Equals(raw, "wechat", StringComparison.OrdinalIgnoreCase))
            return "wechat";
        return "local";
    }

    private static string? FirstSet(string? envValue, string? fileValue)
    {
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue.Trim();
        if (!string.IsNullOrWhiteSpace(fileValue))
            return fileValue.Trim();
        return null;
    }

    private static int ReadInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) ? v : fallback;
    }

    private static bool ReadBool(string name, bool fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (bool.TryParse(raw, out bool b))
            return b;
        if (raw is "1" or "yes" or "YES" or "on" or "ON")
            return true;
        if (raw is "0" or "no" or "NO" or "off" or "OFF")
            return false;
        return fallback;
    }
}

public readonly record struct AutoScaleDefaults(
    int Engines,
    int LineWorkers,
    int DetThreads,
    int RasterWorkers);
