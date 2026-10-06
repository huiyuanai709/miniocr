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

        // Upstream defaults Backend to Auto, which may pick Vulkan. GPU fp16 can change
        // the text, so every graph is pinned unless ocr.backend says otherwise.
        // RecBatchLines is set explicitly, so a GPU backend does not raise it to 16.
        OcrBackend backend = config.Backend switch
        {
            "auto" => OcrBackend.Auto,
            "vulkan" => OcrBackend.Vulkan,
            _ => OcrBackend.Cpu,
        };
        string vulkanName = "";
        ulong vulkanBytes = 0;
        ulong vulkanCap = 0;
        if (config.Backend is "vulkan" or "auto")
        {
            if (!string.IsNullOrWhiteSpace(config.VulkanDevice))
                OcrVulkan.DeviceSelector = config.VulkanDevice;
            OcrVulkan.OnWarning ??= message => logger.LogWarning("{Message}", message);
            OcrVulkan.OnDebug ??= message => logger.LogDebug("{Message}", message);
            if (OcrVulkan.TryProbe() is { } gpu)
            {
                vulkanName = gpu.Name;
                vulkanBytes = gpu.DeviceLocalBytes;
                vulkanCap = gpu.BufferByteCap;
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
                    "Vulkan device {Name} kind={Kind} index={Index} deviceLocalMb={DeviceLocalMb} bufferCapMb={BufferCapMb}",
                    gpu.Name,
                    gpu.Kind,
                    gpu.Index,
                    gpu.DeviceLocalBytes / (1024 * 1024),
                    gpu.BufferByteCap / (1024 * 1024));
            }
        }
        // One detector session and one recognizer session per line worker.
        // The library default (ProcessorCount) would keep a full-size arena
        // on every pooled session, which is how a 2 GB card runs out on the
        // second document.
        bool tightGpuPools = config.Backend == "vulkan" || vulkanName.Length > 0;
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
            "Loading ChineseV6Tiny × {Engines} (backend={Backend}, LineWorkerCount={LineWorkers}, DetIntraOpThreads={DetThreads}, RecIntraOpThreads={RecIntra}, RecBatchLines={RecBatch}, UseCls={UseCls}, DpiDefault={Dpi})",
            pageWorkers,
            config.Backend,
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

        var created = new OcrEngine(engines, config, logger)
        {
            VulkanDeviceName = vulkanName,
            VulkanDeviceLocalBytes = vulkanBytes,
            VulkanBufferCapBytes = vulkanCap,
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
