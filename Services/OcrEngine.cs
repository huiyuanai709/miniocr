using System.Collections.Concurrent;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace MiniOcr.Services;

/// <summary>
/// Owns one or more reusable <see cref="PaddleOcrAll"/> instances as a pool.
/// Page-level work borrows an engine exclusively; LineWorkerCount handles intra-page parallelism.
/// </summary>
public sealed class OcrEngine : IAsyncDisposable
{
    private readonly PaddleOcrAll[] _engines;
    private readonly ConcurrentBag<(PaddleOcrAll Engine, int Index)> _pool;
    private readonly SemaphoreSlim _gate;
    private readonly ILogger<OcrEngine> _logger;

    public bool IsLoaded { get; private set; }
    public int EngineCount => _engines.Length;
    public int LineWorkerCount { get; }
    public int DetIntraOpThreads { get; }
    public bool UseDirectionClassification { get; }
    public string VulkanDeviceName { get; private set; } = "";
    public ulong VulkanDeviceLocalBytes { get; private set; }
    public ulong VulkanBufferCapBytes { get; private set; }
    /// <summary><c>cpu</c>, <c>vulkan</c>, or <c>metal</c>. This is the backend that will run, after a non-macOS <c>metal</c> request falls back to CPU.</summary>
    public string EffectiveBackend { get; private set; } = "cpu";
    /// <summary>Vulkan or Metal device name. Empty on CPU.</summary>
    public string GpuDeviceName { get; private set; } = "";
    /// <summary>Vulkan device-local heap, or Metal <c>recommendedMaxWorkingSetSize</c>, in bytes.</summary>
    public ulong GpuMemoryBytes { get; private set; }
    public ulong GpuBufferCapBytes { get; private set; }
    public OcrRuntimeConfig Config { get; }

    private OcrEngine(
        PaddleOcrAll[] engines,
        OcrRuntimeConfig config,
        ILogger<OcrEngine> logger)
    {
        _engines = engines;
        Config = config;
        LineWorkerCount = config.LineWorkerCount;
        DetIntraOpThreads = config.DetIntraOpThreads;
        UseDirectionClassification = config.UseDirectionClassification;
        _logger = logger;
        _pool = [];
        for (int i = 0; i < engines.Length; i++)
            _pool.Add((engines[i], i));
        _gate = new SemaphoreSlim(engines.Length, engines.Length);
        IsLoaded = true;
    }

    public static async Task<OcrEngine> CreateAsync(
        ILogger<OcrEngine> logger,
        OcrRuntimeConfig? config = null,
        CancellationToken ct = default)
    {
        config ??= OcrRuntimeConfig.FromEnvironment();
        int pageWorkers = config.EngineCount;

        // Upstream defaults Backend to Auto, which may pick Vulkan or, on
        // macOS arm64, Metal. GPU fp16 can change the text, so every graph is
        // pinned unless ocr.backend says otherwise.
        // RecBatchLines is set explicitly, so a GPU backend does not raise it to 16.
        OcrVulkan.OnWarning ??= message => logger.LogWarning("{Message}", message);
        OcrVulkan.OnDebug ??= message => logger.LogDebug("{Message}", message);

        OcrBackend backend = OcrBackend.Cpu;
        string effective = "cpu";
        string vulkanName = "";
        ulong vulkanBytes = 0;
        ulong vulkanCap = 0;
        string gpuName = "";
        ulong gpuBytes = 0;
        ulong gpuCap = 0;
        bool metalOnAppleSilicon = config.Backend == "auto"
            && OperatingSystem.IsMacOS()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

        if (config.Backend == "metal" || metalOnAppleSilicon)
        {
            string hosted = OcrRuntimeConfig.BackendForHost(config.Backend == "metal" ? "metal" : "auto");
            if (config.Backend == "metal" && hosted == "cpu")
            {
                logger.LogWarning("ocr.backend=metal is only supported on macOS; using CPU");
            }
            else if (OcrMetal.TryProbe() is { } metal)
            {
                backend = config.Backend == "metal" ? OcrBackend.Metal : OcrBackend.Auto;
                effective = "metal";
                gpuName = metal.Name;
                gpuBytes = metal.RecommendedMaxWorkingSetBytes;
                gpuCap = metal.BufferByteCap;
                int capped = OcrRuntimeConfig.VulkanEngineCount(gpuBytes, pageWorkers);
                if (capped != pageWorkers)
                {
                    logger.LogWarning(
                        "Metal device {Name} reports {WorkingSetMb} MB recommended working set; engine count {Requested} -> {Capped}",
                        metal.Name,
                        gpuBytes / (1024 * 1024),
                        pageWorkers,
                        capped);
                    pageWorkers = capped;
                    config = config.With(engineCount: capped);
                }
                logger.LogInformation(
                    "Metal device {Name} recommendedWorkingSetMb={WorkingSetMb} bufferCapMb={BufferCapMb}",
                    metal.Name,
                    gpuBytes / (1024 * 1024),
                    gpuCap / (1024 * 1024));
            }
            else if (config.Backend == "metal")
            {
                logger.LogWarning("ocr.backend=metal but no Metal device was found; using CPU");
            }
        }

        if (effective != "metal" && config.Backend is "vulkan" or "auto")
        {
            if (!string.IsNullOrWhiteSpace(config.VulkanDevice))
                OcrVulkan.DeviceSelector = config.VulkanDevice;
            backend = config.Backend == "vulkan" ? OcrBackend.Vulkan : OcrBackend.Auto;
            if (OcrVulkan.TryProbe() is { } gpu)
            {
                vulkanName = gpu.Name;
                vulkanBytes = gpu.DeviceLocalBytes;
                vulkanCap = gpu.BufferByteCap;
                effective = "vulkan";
                gpuName = gpu.Name;
                gpuBytes = gpu.DeviceLocalBytes;
                gpuCap = gpu.BufferByteCap;
                int capped = OcrRuntimeConfig.VulkanEngineCount(gpu.DeviceLocalBytes, pageWorkers);
                if (capped != pageWorkers)
                {
                    logger.LogWarning(
                        "Vulkan device {Name} reports {DeviceLocalMb} MB device-local memory; engine count {Requested} -> {Capped}",
                        gpu.Name,
                        gpu.DeviceLocalBytes / (1024 * 1024),
                        pageWorkers,
                        capped);
                    pageWorkers = capped;
                    config = config.With(engineCount: capped);
                }
                logger.LogInformation(
                    "Vulkan device {Name} kind={Kind} index={Index} deviceLocalMb={DeviceLocalMb} bufferCapMb={BufferCapMb} coopGemm={Coop}",
                    gpu.Name,
                    gpu.Kind,
                    gpu.Index,
                    gpu.DeviceLocalBytes / (1024 * 1024),
                    gpu.BufferByteCap / (1024 * 1024),
                    OcrVulkan.CoopGemm);
                if (Environment.GetEnvironmentVariable("MINIOCR_REC_BATCH") is null)
                {
                    int limited = OcrVulkan.LimitRecBatch(config.RecBatchLines, vulkanBytes, OcrVulkan.CoopGemm);
                    if (limited != config.RecBatchLines)
                    {
                        logger.LogWarning(
                            "Vulkan device {Name} deviceLocalMb={Mb} coopGemm={Coop}; rec batch {From} -> {To}",
                            gpu.Name,
                            vulkanBytes / (1024 * 1024),
                            OcrVulkan.CoopGemm,
                            config.RecBatchLines,
                            limited);
                        config = config.With(recBatchLines: limited);
                    }
                }
            }
            else if (config.Backend == "vulkan")
            {
                // The library still accepts OcrBackend.Vulkan and serves each
                // image from CPU when the device is missing.
                effective = "cpu";
            }
        }
        // One detector session and one recognizer session per line worker.
        // The library default (ProcessorCount) would keep a full-size arena
        // on every pooled session, which is how a 2 GB card runs out on the
        // second document.
        bool tightGpuPools = effective is "vulkan" or "metal";
        int recPool = Math.Max(1, config.LineWorkerCount);
        var options = new PaddleOcrOptions
        {
            LineWorkerCount = config.LineWorkerCount,
            DetIntraOpThreads = config.DetIntraOpThreads,
            UseDirectionClassification = config.UseDirectionClassification,
            RecBatchLines = config.RecBatchLines,
            RecIntraOpThreads = config.RecIntraOpThreads,
            Detector = new PaddleOcrDetectorOptions
            {
                LimitSideLength = config.DetLimitSideLength,
                Backend = backend,
                MaxPooledSessions = tightGpuPools ? 1 : Environment.ProcessorCount,
            },
            Classifier = new PaddleOcrClassifierOptions
            {
                Backend = backend,
                MaxPooledSessions = tightGpuPools ? 1 : Environment.ProcessorCount,
            },
            Recognizer = new PaddleOcrRecognizerOptions
            {
                Backend = backend,
                MaxPooledSessions = tightGpuPools ? recPool : Environment.ProcessorCount,
            },
        };

        PaddleOcrModelBundle bundle = ChineseV6TinyModels.Default;
        if (!config.UseDirectionClassification)
        {
            // Skip loading CLS weights entirely — saves RAM × engine count and avoids CLS compute.
            bundle = new PaddleOcrModelBundle(
                bundle.Name + "-nocls",
                bundle.LanguageCode,
                bundle.Detection,
                bundle.Recognition,
                bundle.Dictionary,
                classification: null!);
        }

        logger.LogInformation(
            "Loading ChineseV6Tiny × {Engines} (backend={Backend}, effective={Effective}, LineWorkerCount={LineWorkers}, DetIntraOpThreads={DetThreads}, RecIntraOpThreads={RecIntra}, RecBatchLines={RecBatch}, UseCls={UseCls}, DpiDefault={Dpi})",
            pageWorkers,
            config.Backend,
            effective,
            options.LineWorkerCount,
            options.DetIntraOpThreads,
            options.RecIntraOpThreads,
            options.RecBatchLines,
            options.UseDirectionClassification,
            config.DefaultDpi);

        PaddleOcrAll[] engines = new PaddleOcrAll[pageWorkers];
        try
        {
            Task<PaddleOcrAll>[] loads = new Task<PaddleOcrAll>[pageWorkers];
            for (int i = 0; i < pageWorkers; i++)
            {
                ct.ThrowIfCancellationRequested();
                loads[i] = PaddleOcrAll.LoadAsync(bundle, options, ct);
            }

            PaddleOcrAll[] loaded = await Task.WhenAll(loads).ConfigureAwait(false);
            for (int i = 0; i < pageWorkers; i++)
                engines[i] = loaded[i];
        }
        catch
        {
            foreach (PaddleOcrAll? e in engines)
                e?.Dispose();
            throw;
        }

        if (effective is "vulkan" or "metal")
        {
            foreach (PaddleOcrAll loaded in engines)
                loaded.Warmup();
            OcrVulkan.GpuTimingSnapshot warm = OcrVulkan.ReadTimings();
            long cacheBytes = 0;
            try
            {
                if (File.Exists(OcrVulkan.PipelineCachePath))
                    cacheBytes = new FileInfo(OcrVulkan.PipelineCachePath).Length;
            }
            catch (IOException) { }
            logger.LogInformation(
                "GPU warmup initMs={InitMs:F0} fence={Fence} device={Device} pipelineCache={Cache} restored={Restored} cacheBytes={CacheBytes} gpuRuns={GpuRuns} deviceLost={DeviceLost}",
                warm.InitMs,
                warm.FenceWait,
                string.IsNullOrEmpty(gpuName) ? warm.DeviceName : gpuName,
                string.IsNullOrEmpty(OcrVulkan.PipelineCachePath) ? "(memory)" : OcrVulkan.PipelineCachePath,
                OcrVulkan.PipelineCacheRestored,
                cacheBytes,
                warm.GpuRuns,
                OcrVulkan.DeviceLostCount);
        }

        var created = new OcrEngine(engines, config, logger)
        {
            VulkanDeviceName = vulkanName,
            VulkanDeviceLocalBytes = vulkanBytes,
            VulkanBufferCapBytes = vulkanCap,
            EffectiveBackend = effective,
            GpuDeviceName = gpuName,
            GpuMemoryBytes = gpuBytes,
            GpuBufferCapBytes = gpuCap,
        };
        return created;
    }

    public async Task<T> UseAsync<T>(Func<PaddleOcrAll, T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (!_pool.TryTake(out var leased))
        {
            _gate.Release();
            throw new InvalidOperationException("OCR engine pool exhausted unexpectedly.");
        }

        try
        {
            return work(leased.Engine);
        }
        finally
        {
            _pool.Add(leased);
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!IsLoaded)
            return ValueTask.CompletedTask;
        IsLoaded = false;
        foreach (PaddleOcrAll e in _engines)
            e.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
