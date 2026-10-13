using System.Collections.Concurrent;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Medium;
using Sdcb.SimdPaddleOCR.Models.SealV4Mobile;
using Sdcb.SimdPaddleOCR.Models.SealV4Server;
using SkiaSharp;

namespace MiniOcr.Services;

/// <summary>
/// Optional pool of <see cref="PaddleOcrSealAll"/>. Detection is SealV4 mobile
/// (default) or server; recognition is the same ChineseV6Medium weights as body OCR.
/// When an embedded model cannot be opened, <see cref="IsLoaded"/> stays false and
/// the caller keeps the red-blob polar unwrap.
/// </summary>
public sealed class SealOcrEngine : IDisposable
{
    private readonly PaddleOcrSealAll[] _engines;
    private readonly ConcurrentBag<PaddleOcrSealAll> _pool;
    private readonly SemaphoreSlim _gate;
    private int _disposed;

    public bool IsLoaded { get; }
    /// <summary><c>mobile</c> or <c>server</c>.</summary>
    public string Model { get; }
    public int EngineCount => _engines.Length;

    private SealOcrEngine(PaddleOcrSealAll[] engines, string model)
    {
        _engines = engines;
        Model = model;
        IsLoaded = engines.Length > 0;
        _pool = [];
        foreach (PaddleOcrSealAll engine in engines)
            _pool.Add(engine);
        int slots = Math.Max(1, engines.Length);
        _gate = new SemaphoreSlim(slots, slots);
    }

    public static SealOcrEngine Unavailable(string model) => new([], model);

    public static async Task<SealOcrEngine> CreateAsync(
        ILogger logger,
        OcrRuntimeConfig config,
        string effectiveBackend,
        CancellationToken ct = default)
    {
        string model = config.SealModel;
        IPaddleOcrModelProvider detection = model == "server"
            ? SealV4ServerModel.Detection
            : SealV4MobileModel.Detection;
        if (!CanOpen(detection)
            || !CanOpen(ChineseV6MediumModel.Recognition)
            || !CanOpen(ChineseV6MediumModel.Dictionary))
        {
            logger.LogWarning(
                "Seal OCR models are not available ({Model} detector or ChineseV6Medium recognizer). Using red-blob polar unwrap.",
                model);
            return Unavailable(model);
        }

        OcrBackend backend = effectiveBackend switch
        {
            "vulkan" => OcrBackend.Vulkan,
            "metal" => OcrBackend.Metal,
            _ => OcrBackend.Cpu,
        };
        bool tightGpu = backend is OcrBackend.Vulkan or OcrBackend.Metal;
        int count = Math.Clamp(config.EngineCount, 1, 16);
        var options = new PaddleOcrSealOptions
        {
            DetIntraOpThreads = config.DetIntraOpThreads,
            Detector = new PaddleOcrSealDetectorOptions
            {
                Backend = backend,
                MaxPooledSessions = tightGpu ? 1 : Environment.ProcessorCount,
            },
            Recognizer = new PaddleOcrRecognizerOptions
            {
                Backend = backend,
                MaxPooledSessions = tightGpu ? 1 : Environment.ProcessorCount,
            },
        };

        logger.LogInformation(
            "Loading PaddleOcrSealAll {Model} × {Engines} (backend={Backend})",
            model,
            count,
            effectiveBackend);

        PaddleOcrSealAll[] engines = new PaddleOcrSealAll[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                engines[i] = await PaddleOcrSealAll.LoadAsync(
                    detection,
                    ChineseV6MediumModel.Recognition,
                    ChineseV6MediumModel.Dictionary,
                    options,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (PaddleOcrSealAll? loaded in engines)
                loaded?.Dispose();
            logger.LogWarning(ex, "PaddleOcrSealAll failed to load. Using red-blob polar unwrap.");
            return Unavailable(model);
        }

        return new SealOcrEngine(engines, model);
    }

    internal Task<string> ReadAsync(PageImage image, CancellationToken ct)
    {
        if (!IsLoaded)
            return Task.FromResult("");
        if (image.Pixels is { } pixels)
        {
            if (pixels.ColorType != SKColorType.Bgra8888)
                return Task.FromResult("");
            int width = pixels.Width;
            int height = pixels.Height;
            int stride = pixels.RowBytes;
            ReadOnlyMemory<byte> memory = pixels.Pixels;
            return RunAsync(seal => seal.Run(memory.Span, width, height, stride, ImagePixelFormat.Bgra32), ct);
        }

        return ReadAsync(image.RequireBitmap(), ct);
    }

    public async Task<string> ReadAsync(SKBitmap bitmap, CancellationToken ct)
    {
        if (!IsLoaded || bitmap is null)
            return "";
        EnsureBgra(bitmap, out SKBitmap working, out bool owned);
        try
        {
            int width = working.Width;
            int height = working.Height;
            int stride = working.RowBytes;
            IntPtr pixels = working.GetPixels();
            int byteCount = stride * height;
            if (pixels == IntPtr.Zero || byteCount <= 0 || working.ColorType != SKColorType.Bgra8888)
                return "";
            return await RunAsync(seal =>
            {
                unsafe
                {
                    ReadOnlySpan<byte> span = new((void*)pixels, byteCount);
                    return seal.Run(span, width, height, stride, ImagePixelFormat.Bgra32);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (owned)
                working.Dispose();
        }
    }

    private async Task<string> RunAsync(Func<PaddleOcrSealAll, PaddleOcrSealResult> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (!_pool.TryTake(out PaddleOcrSealAll? leased))
        {
            _gate.Release();
            throw new InvalidOperationException("Seal OCR engine pool exhausted unexpectedly.");
        }

        try
        {
            return Join(work(leased));
        }
        finally
        {
            _pool.Add(leased);
            _gate.Release();
        }
    }

    private static string Join(PaddleOcrSealResult result)
    {
        if (result.Lines.Length == 0)
            return "";
        List<string> lines = new(result.Lines.Length);
        foreach (PaddleOcrSealLine line in result.Lines)
        {
            string text = line.Text?.Replace("\r", "", StringComparison.Ordinal).Trim() ?? "";
            if (text.Length > 0)
                lines.Add(text);
        }

        return string.Join('\n', lines);
    }

    private static bool CanOpen(IPaddleOcrModelProvider provider)
    {
        try
        {
            using Stream stream = provider.OpenRead();
            return stream.CanRead && (!stream.CanSeek || stream.Length > 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void EnsureBgra(SKBitmap source, out SKBitmap working, out bool owned)
    {
        if (source.ColorType == SKColorType.Bgra8888)
        {
            working = source;
            owned = false;
            return;
        }

        working = source.Copy(SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Failed to convert SKBitmap to BGRA8888.");
        owned = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (PaddleOcrSealAll engine in _engines)
            engine.Dispose();
        _gate.Dispose();
    }
}
