using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Throughput knobs from AppData config.json + environment overrides.
/// Env (MINIOCR_*) wins over file for OCR. Auto-scales from ProcessorCount when
/// <c>autoScaleFromCpu</c> is true and engines/workers are unset.
/// </summary>
public sealed class OcrRuntimeConfig
{
    /// <summary><c>local</c> (Paddle), <c>llm</c> (vision), or <c>hunyuan</c> (local HunyuanOCR server). Default local.</summary>
    public string Mode { get; init; } = "local";
    public int EngineCount { get; init; }
    public int DefaultDpi { get; init; }
    public int LineWorkerCount { get; init; }
    public int DetIntraOpThreads { get; init; }
    public bool UseDirectionClassification { get; init; }
    public int RasterWorkerCount { get; init; }
    public int RecBatchLines { get; init; }
    public int DetLimitSideLength { get; init; }
    public bool AutoScaleFromCpu { get; init; }
    public int ProcessorCount { get; init; }

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

        // Recompute line/det defaults against the *resolved* engine count so
        // engines*(line+det) stays near 1–1.5× cores even when engines is overridden.
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
        // hunyuan default 144: the 1B VLM wants a sharper page than the cloud-vision default.
        // Explicit ocr.dpi or MINIOCR_DPI always wins — do not change local's 96 when mode=local.
        int dpiFallback = ocr.Dpi ?? mode switch
        {
            "llm" => 72,
            "hunyuan" => 144,
            _ => 96,
        };
        int dpi = Math.Clamp(ReadInt("MINIOCR_DPI", dpiFallback), 36, 300);

        bool useClsFile = ocr.UseCls ?? false;
        bool useCls = ReadBool("MINIOCR_USE_CLS", useClsFile);

        // Remote vision (llm / hunyuan) is IO-bound relative to PDFium: default raster workers = min(8, cores).
        bool remoteVision = mode is "llm" or "hunyuan";
        int rasterDefault;
        if (remoteVision)
            rasterDefault = Math.Clamp(Math.Min(8, cores), 1, 8);
        else if (autoScale)
            rasterDefault = forEngines.RasterWorkers;
        else
            rasterDefault = Math.Clamp(Math.Min(engines, 4), 1, 8);
        int raster = ResolveInt(
            "MINIOCR_RASTER_WORKERS",
            ocr.RasterWorkers,
            rasterDefault,
            autoScale || remoteVision,
            rasterDefault);
        raster = Math.Clamp(raster, 1, 8);

        int recBatch = Math.Clamp(ReadInt("MINIOCR_REC_BATCH", 8), 1, 64);
        int detLimit = Math.Clamp(ReadInt("MINIOCR_DET_LIMIT_SIDE", 960), 64, 4096);

        return new OcrRuntimeConfig
        {
            Mode = mode,
            EngineCount = engines,
            DefaultDpi = dpi,
            LineWorkerCount = line,
            DetIntraOpThreads = det,
            UseDirectionClassification = useCls,
            RasterWorkerCount = raster,
            RecBatchLines = recBatch,
            DetLimitSideLength = detLimit,
            AutoScaleFromCpu = autoScale,
            ProcessorCount = cores,
        };
    }

    /// <summary>
    /// New curve (2–64 cores): engines = Clamp(cores/2, 1, min(16, cores)).
    /// Old awkward default forced min 4 engines even on 2-core boxes.
    /// Workers chosen so engines*(line+det) ≈ 1.0–1.5× cores.
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

        // Target total OCR threads ≈ 1.25× cores (band 1.0–1.5×).
        int target = Math.Max(engines * 2, (int)Math.Round(cores * 1.25));
        int maxBudget = Math.Max(engines * 2, (int)Math.Floor(cores * 1.5));
        target = Math.Min(target, maxBudget);

        int perEngine = Math.Max(2, (target + engines - 1) / engines);
        // Prefer slightly more line workers than det (CLS/REC parallel vs DET intra-op).
        int line = Math.Clamp((perEngine + 1) / 2, 1, 8);
        int det = Math.Clamp(perEngine - line, 1, 8);

        // Shrink if still oversubscribed.
        while (engines * (line + det) > maxBudget && (line > 1 || det > 1))
        {
            if (line >= det && line > 1)
                line--;
            else if (det > 1)
                det--;
            else
                break;
        }

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
    /// Copy with a different OCR mode, re-applying <c>MINIOCR_OCR_MODE</c>.
    /// Startup fallback must use <see cref="ForceMode"/> so an unusable env mode
    /// can actually drop back to local.
    /// </summary>
    public OcrRuntimeConfig WithMode(string mode) => CopyWithMode(ResolveMode(mode));

    /// <summary>Set mode literally. Does not re-read <c>MINIOCR_OCR_MODE</c>.</summary>
    public OcrRuntimeConfig ForceMode(string mode) => CopyWithMode(NormalizeMode(mode));

    private OcrRuntimeConfig CopyWithMode(string mode) => new()
    {
        Mode = mode,
        EngineCount = EngineCount,
        DefaultDpi = DefaultDpi,
        LineWorkerCount = LineWorkerCount,
        DetIntraOpThreads = DetIntraOpThreads,
        UseDirectionClassification = UseDirectionClassification,
        RasterWorkerCount = RasterWorkerCount,
        RecBatchLines = RecBatchLines,
        DetLimitSideLength = DetLimitSideLength,
        AutoScaleFromCpu = AutoScaleFromCpu,
        ProcessorCount = ProcessorCount,
    };

    public bool IsLlmMode =>
        string.Equals(Mode, "llm", StringComparison.OrdinalIgnoreCase);

    public bool IsHunyuanMode =>
        string.Equals(Mode, "hunyuan", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Env MINIOCR_OCR_MODE overrides file. Accepts local|llm|hunyuan (case-insensitive).
    /// Unknown values fall back to local.
    /// </summary>
    public static string ResolveMode(string? fileMode)
    {
        string? env = Environment.GetEnvironmentVariable("MINIOCR_OCR_MODE");
        string raw = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (fileMode ?? "local");
        return NormalizeMode(raw);
    }

    /// <summary>Map a raw mode string. Unknown values become <c>local</c>.</summary>
    public static string NormalizeMode(string? raw)
    {
        if (string.Equals(raw, "llm", StringComparison.OrdinalIgnoreCase))
            return "llm";
        if (string.Equals(raw, "hunyuan", StringComparison.OrdinalIgnoreCase))
            return "hunyuan";
        return "local";
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
