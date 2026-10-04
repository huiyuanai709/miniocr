using System.Diagnostics;
using System.Threading.Channels;
using MiniOcr.Models;
using PDFtoImage;
using PDFtoImage.Parallel;
using Sdcb.SimdPaddleOCR;
using SkiaSharp;

namespace MiniOcr.Services;

public sealed class PdfOcrPipeline : IAsyncDisposable
{
    private readonly OcrEngine? _engine;
    private readonly WeChatOcrEngine? _wechat;
    private readonly ILogger<PdfOcrPipeline> _logger;
    private readonly LlmEntityExtractor? _llm;
    private readonly LlmVisionOcr? _vision;
    private readonly OcrRuntimeConfig _config;
    private readonly ClusterCoordinator? _cluster;
    private readonly int _pageWindow;
    private readonly int _defaultDpi;
    private readonly int _rasterWorkers;
    private readonly object _parallelGate = new();
    private readonly SemaphoreSlim _mapGate = new(1, 1);
    private readonly List<MappedPdf> _mapped = [];
    private ParallelPdfProcessor? _parallel;
    private int _parallelDisposed;

    public PdfOcrPipeline(
        OcrRuntimeConfig config,
        ILogger<PdfOcrPipeline> logger,
        OcrEngine? engine = null,
        LlmEntityExtractor? llm = null,
        LlmVisionOcr? vision = null,
        WeChatOcrEngine? wechat = null,
        ClusterCoordinator? cluster = null,
        ParallelPdfProcessor? parallelRenderer = null)
    {
        _config = config;
        _logger = logger;
        _engine = engine;
        _wechat = wechat;
        _llm = llm;
        _vision = vision;
        _cluster = cluster;
        _defaultDpi = config.DefaultDpi;
        _rasterWorkers = config.RasterWorkerCount;
        if (config.IsParallelRender)
            _parallel = parallelRenderer;

        if (config.IsLlmMode)
        {
            if (vision is null || !vision.IsUsable)
                throw new InvalidOperationException(
                    "ocr.mode=llm requires a usable LLM (enabled + apiKey + baseUrl + model).");
            _pageWindow = Math.Max(4, Math.Min(vision.OcrConcurrency, 64));
        }
        else if (config.IsWeChatMode)
        {
            if (wechat is null || !wechat.IsReady)
                throw new InvalidOperationException("ocr.mode=wechat requires a connected WeChat OCR engine.");
            _pageWindow = Math.Max(4, wechat.InstanceCount * 2);
        }
        else
        {
            if (engine is null)
                throw new InvalidOperationException("ocr.mode=local requires OcrEngine.");
            _pageWindow = Math.Max(4, engine.EngineCount * 2);
        }
    }

    public Task<OcrResponse> ProcessAsync(
        RentedBuffer pdf,
        double downloadMs,
        string downloadMode,
        CancellationToken ct,
        int? dpiOverride = null,
        string? sourceUrl = null)
    {
        Stopwatch totalSw = Stopwatch.StartNew();
        byte[] array = pdf.DangerousGetArray();
        int length = pdf.Length;
        return ProcessCoreAsync(array, length, downloadMs, downloadMode, totalSw, dpiOverride, sourceUrl, ct);
    }

    private async Task<OcrResponse> ProcessCoreAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        int? dpiOverride,
        string? sourceUrl,
        CancellationToken ct)
    {
        try
        {
        int pageCount;
        using (MemoryStream countStream = new(pdfBytes, index: 0, count: pdfByteCount, writable: false, publiclyVisible: true))
            pageCount = Conversion.GetPageCount(countStream, leaveOpen: false);

        if (pageCount <= 0)
            throw new InvalidOperationException("PDF has no pages.");
        if (pageCount > 2000)
            throw new InvalidOperationException($"PDF has {pageCount} pages; max supported is 2000.");

        int dpi = Math.Clamp(dpiOverride ?? _defaultDpi, 36, 300);

        // No cluster config (or no remote nodes) keeps the single-node path below unchanged.
        if (_cluster?.ShouldDistribute() == true)
        {
            _logger.LogInformation(
                "OCR pipeline (cluster): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, mode={Mode}",
                pageCount, pdfByteCount, dpi, _config.Mode);
            return await ProcessDistributedAsync(
                pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, sourceUrl, totalSw, ct)
                .ConfigureAwait(false);
        }

        if (_config.IsLlmMode)
        {
            _logger.LogInformation(
                "OCR pipeline (llm vision): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, ocrConcurrency={Conc}, rasterWorkers={Raster}",
                pageCount, pdfByteCount, dpi, _vision!.OcrConcurrency, _rasterWorkers);
            return await ProcessWithVisionAsync(
                pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct)
                .ConfigureAwait(false);
        }

        if (_config.IsWeChatMode)
        {
            _logger.LogInformation(
                "OCR pipeline (wechat {Kind}): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, instances={Instances}, rasterWorkers={Raster}",
                _wechat!.KindName, pageCount, pdfByteCount, dpi, _wechat.InstanceCount, _rasterWorkers);
            return await ProcessWithTextEngineAsync(
                pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct,
                modeLabel: "wechat",
                workers: _wechat.InstanceCount,
                recognize: (bitmap, token) => _wechat.RecognizeBitmapAsync(bitmap, token))
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "OCR pipeline (local): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, engines={Engines}, rasterWorkers={Raster}",
            pageCount, pdfByteCount, dpi, _engine!.EngineCount, _rasterWorkers);

        return await ProcessWithTextEngineAsync(
            pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct,
            modeLabel: "local",
            workers: _engine.EngineCount,
            recognize: RecognizeLocalAsync)
            .ConfigureAwait(false);
        }
        finally
        {
            ReleaseMappedPdf(pdfBytes);
        }
    }

    private async Task<OcrResponse> ProcessWithVisionAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        int pageCount,
        int dpi,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        RenderOptions renderOptions = CreateRenderOptions(dpi);
        int concurrency = _vision!.OcrConcurrency;
        int jpegQuality = _vision.OcrJpegQuality;

        // Overlap raster→encode→vision (same idea as local Channel pipeline).
        // Do NOT wait for all pages before first RecognizePageAsync.
        Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
            Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(_pageWindow)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // Bounded JPEG queue: ~2× vision concurrency (or pageWindow), never hold all page JPEGs.
        int jpegCapacity = Math.Min(
            Math.Max(1, pageCount),
            Math.Max(2, Math.Max(_pageWindow, concurrency * 2)));
        Channel<PageJpeg> jpegs = Channel.CreateBounded<PageJpeg>(new BoundedChannelOptions(jpegCapacity)
        {
            SingleWriter = false,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        OcrPageResult[] pages = new OcrPageResult[pageCount];
        double rasterTotal = 0;
        double ocrTotal = 0;
        object timingLock = new();
        int firstJpegFlag = 0;
        int firstVisionFlag = 0;

        // Wall clock from download handoff → first JPEG / first vision (overlap proof).
        Stopwatch sinceDownload = Stopwatch.StartNew();
        _logger.LogInformation(
            "Vision pipeline: download done (mode={Mode}, downloadMs={DownloadMs:F1}); " +
            "starting overlapped raster+encode→vision; pages={Pages}, dpi={Dpi}, " +
            "jpegQuality={JpegQ}, rasterWorkers={Raster}, ocrConcurrency={Conc}, " +
            "jpegQueueCapacity={JpegCap}",
            downloadMode,
            downloadMs,
            pageCount,
            dpi,
            jpegQuality,
            _rasterWorkers,
            concurrency,
            jpegCapacity);

        TextLayerPlan visionPlan = PlanTextLayer(pdfBytes, pdfByteCount, pageCount);
        PlacePrepared(pages, visionPlan, ner: null);
        Task producer = ProduceIndicesAsync(
            pdfBytes, pdfByteCount, visionPlan.OcrIndices, renderOptions, rasterized.Writer, ct);

        async Task EncodeConsumerAsync()
        {
            await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct)
                               .ConfigureAwait(false))
            {
                using (bitmap)
                {
                    int width = bitmap.Width;
                    int height = bitmap.Height;
                    byte[] jpeg = LlmVisionOcr.EncodeJpeg(bitmap, jpegQuality);
                    PageJpeg item = new(index, width, height, jpeg, rasterMs);
                    lock (timingLock)
                        rasterTotal += rasterMs;

                    if (Interlocked.CompareExchange(ref firstJpegFlag, 1, 0) == 0)
                    {
                        _logger.LogInformation(
                            "Vision pipeline: first page JPEG ready (page={Page}, {W}x{H}, jpegBytes={Bytes}) " +
                            "at t+{Elapsed:F0}ms after download handoff",
                            index + 1,
                            width,
                            height,
                            jpeg.Length,
                            sinceDownload.Elapsed.TotalMilliseconds);
                    }

                    await jpegs.Writer.WriteAsync(item, ct).ConfigureAwait(false);
                }
            }
        }

        async Task VisionConsumerAsync()
        {
            await foreach (PageJpeg img in jpegs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (img.Jpeg is null)
                    continue;

                if (Interlocked.CompareExchange(ref firstVisionFlag, 1, 0) == 0)
                {
                    _logger.LogInformation(
                        "Vision pipeline: first vision request (page={Page}) " +
                        "at t+{Elapsed:F0}ms after download handoff (overlap vs waiting for all pages)",
                        img.Index + 1,
                        sinceDownload.Elapsed.TotalMilliseconds);
                }

                OcrPageResult page = await _vision
                    .RecognizePageAsync(img.Index + 1, img.Width, img.Height, img.Jpeg, img.RasterMs, ct)
                    .ConfigureAwait(false);
                pages[img.Index] = page;
                lock (timingLock)
                    ocrTotal += page.OcrMs;
            }
        }

        // Encode is CPU; scale with raster workers (vision is IO so CPU free for PDFium+JPEG).
        int encodeWorkers = Math.Clamp(_rasterWorkers, 1, 8);
        Task[] encoders = Enumerable.Range(0, encodeWorkers)
            .Select(_ => EncodeConsumerAsync())
            .ToArray();

        int visionWorkers = Math.Clamp(concurrency, 1, Math.Max(1, pageCount));
        Task[] visionTasks = Enumerable.Range(0, visionWorkers)
            .Select(_ => VisionConsumerAsync())
            .ToArray();

        try
        {
            await producer.ConfigureAwait(false);
            await Task.WhenAll(encoders).ConfigureAwait(false);
        }
        finally
        {
            jpegs.Writer.TryComplete();
        }

        await Task.WhenAll(visionTasks).ConfigureAwait(false);

        for (int i = 0; i < pageCount; i++)
        {
            if (pages[i] is null)
                throw new InvalidOperationException($"Missing vision OCR result for page index {i}.");
        }

        // Entities derived from vision ruleList (for any consumer of OcrResponse.Entities).
        OcrEntities entities = EntitiesFromVisionPages(pages);
        List<OcrPageResult> visible = VisiblePages(pages);

        totalSw.Stop();

        _logger.LogInformation(
            "Vision pipeline done: firstJpegLogged={FirstJpeg}, firstVisionLogged={FirstVision}, " +
            "totalMs={Total:F1}",
            firstJpegFlag == 1,
            firstVisionFlag == 1,
            totalSw.Elapsed.TotalMilliseconds);

        LogTextHash(pages);
        OcrResponse response = new()
        {
            Ok = true,
            PageCount = pageCount,
            PdfBytes = pdfByteCount,
            DownloadMode = downloadMode,
            Dpi = dpi,
            Timings = new OcrTimings
            {
                DownloadMs = Math.Round(downloadMs, 1),
                RasterizeMs = Math.Round(rasterTotal, 1),
                OcrMs = Math.Round(ocrTotal, 1),
                TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            },
            Pages = visible,
            Entities = entities,
        };
        StampSources(response, pages);
        return response;
    }

    private async Task<string> RecognizeLocalAsync(SKBitmap bitmap, CancellationToken ct)
    {
        EnsureBgra8888(bitmap, out SKBitmap working, out bool ownedWorking);
        try
        {
            int width = working.Width;
            int height = working.Height;
            int stride = working.RowBytes;
            IntPtr pixels = working.GetPixels();
            int byteCount = stride * height;
            PaddleOcrResult result = await _engine!.UseAsync(ocr =>
            {
                unsafe
                {
                    ReadOnlySpan<byte> span = new((void*)pixels, byteCount);
                    return ocr.Run(span, width, height, stride, ImagePixelFormat.Bgra32);
                }
            }, ct).ConfigureAwait(false);
            return result.Text?.Replace("\r", "").Trim() ?? "";
        }
        finally
        {
            if (ownedWorking)
                working.Dispose();
        }
    }

    private async Task<OcrResponse> ProcessWithTextEngineAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        int pageCount,
        int dpi,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        CancellationToken ct,
        string modeLabel,
        int workers,
        Func<SKBitmap, CancellationToken, Task<string>> recognize)
    {
        RenderOptions renderOptions = CreateRenderOptions(dpi);

        OcrPageResult[] pages = new OcrPageResult[pageCount];

        Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
            Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(_pageWindow)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        double rasterTotal = 0;
        double ocrTotal = 0;
        object timingLock = new();

        using CancellationTokenSource llmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LlmEntityExtractor.LlmExtractionSession? ner = _llm is { IsUsable: true }
            ? _llm.Begin(pageCount, llmCts.Token)
            : null;

        TextLayerPlan plan = PlanTextLayer(pdfBytes, pdfByteCount, pageCount);
        PlacePrepared(pages, plan, ner);
        Task producer = ProduceIndicesAsync(
            pdfBytes, pdfByteCount, plan.OcrIndices, renderOptions, rasterized.Writer, ct);

        async Task ConsumerAsync()
        {
            await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct)
                               .ConfigureAwait(false))
            {
                using (bitmap)
                {
                    Stopwatch ocrSw = Stopwatch.StartNew();
                    int width = bitmap.Width;
                    int height = bitmap.Height;
                    string pageText = await recognize(bitmap, ct).ConfigureAwait(false);
                    ocrSw.Stop();
                    pageText = pageText.Replace("\r", "").Trim();

                    pages[index] = new OcrPageResult
                    {
                        Page = index + 1,
                        Width = width,
                        Height = height,
                        Text = pageText,
                        Source = PdfTextLayer.SourceOcr,
                        RasterizeMs = Math.Round(rasterMs, 1),
                        OcrMs = Math.Round(ocrSw.Elapsed.TotalMilliseconds, 1),
                    };

                    lock (timingLock)
                    {
                        rasterTotal += rasterMs;
                        ocrTotal += ocrSw.Elapsed.TotalMilliseconds;
                    }

                    if (string.Equals(modeLabel, "wechat", StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "WeChat OCR page {Page}/{Total} ocrMs={Ms:F1} chars={Chars}",
                            index + 1,
                            pageCount,
                            ocrSw.Elapsed.TotalMilliseconds,
                            pageText.Length);
                    }

                    // Queue a 10-page NER group as soon as those pages exist,
                    // while later pages are still in OCR.
                    ner?.Add(pages[index]);
                }
            }
        }

        Task[] consumers = Enumerable.Range(0, Math.Max(1, workers))
            .Select(_ => ConsumerAsync())
            .ToArray();

        Exception? failure = null;
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            llmCts.Cancel();
        }

        try
        {
            await Task.WhenAll(consumers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure ??= ex;
            llmCts.Cancel();
        }

        if (failure is not null)
        {
            if (ner is not null)
                await ner.AbandonAsync().ConfigureAwait(false);
            throw failure;
        }

        for (int i = 0; i < pageCount; i++)
        {
            if (pages[i] is null)
            {
                llmCts.Cancel();
                if (ner is not null)
                    await ner.AbandonAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Missing OCR result for page {i + 1}.");
            }
        }

        OcrEntities entities = await ExtractEntitiesAsync(pages, ner).ConfigureAwait(false);
        List<OcrPageResult> visible = VisiblePages(pages);

        totalSw.Stop();
        LogTextHash(pages);

        if (string.Equals(modeLabel, "wechat", StringComparison.Ordinal))
        {
            double msPerPage = pageCount > 0 ? ocrTotal / pageCount : 0;
            string preview = "";
            foreach (OcrPageResult page in pages)
            {
                if (!string.IsNullOrWhiteSpace(page.Text))
                {
                    preview = page.Text.Length <= 120 ? page.Text : page.Text[..120];
                    preview = preview.Replace('\n', ' ');
                    break;
                }
            }

            _logger.LogInformation(
                "WeChat OCR done: kind={Kind} instances={Instances} pages={Pages} ocrMs={Ocr:F1} ms/page={Avg:F1} preview={Preview}",
                _wechat!.KindName,
                _wechat.InstanceCount,
                pageCount,
                ocrTotal,
                msPerPage,
                preview);
        }

        OcrResponse response = new()
        {
            Ok = true,
            PageCount = pageCount,
            PdfBytes = pdfByteCount,
            DownloadMode = downloadMode,
            Dpi = dpi,
            Timings = new OcrTimings
            {
                DownloadMs = Math.Round(downloadMs, 1),
                RasterizeMs = Math.Round(rasterTotal, 1),
                OcrMs = Math.Round(ocrTotal, 1),
                TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            },
            Pages = visible,
            Entities = entities,
        };
        StampSources(response, pages);
        return response;
    }

    /// <summary>
    /// Coordinator path. Remote nodes pull the PDF and page batches; this process is the
    /// local node. When <c>cluster.distributedNer</c> is on, each LLM-capable node extracts
    /// names for NER groups it claims. Otherwise NER still sees pages through
    /// <see cref="LlmEntityExtractor.LlmExtractionSession.Add"/> as soon as each page is committed.
    /// </summary>
    private async Task<OcrResponse> ProcessDistributedAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        int pageCount,
        int dpi,
        double downloadMs,
        string downloadMode,
        string? sourceUrl,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        bool vision = _config.IsLlmMode;
        bool distributeNer = !vision && _cluster!.DistributedNer;
        using CancellationTokenSource llmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LlmEntityExtractor.LlmExtractionSession? ner = !vision && !distributeNer && _llm is { IsUsable: true }
            ? _llm.Begin(pageCount, llmCts.Token)
            : null;

        OcrPageResult[] pages = new OcrPageResult[pageCount];
        double rasterTotal = 0;
        double ocrTotal = 0;
        object timingLock = new();

        void Accept(OcrPageResult page)
        {
            if (!vision)
                page.RuleList = null;
            int index = page.Page - 1;
            if ((uint)index >= (uint)pageCount)
                return;
            pages[index] = page;
            lock (timingLock)
            {
                rasterTotal += page.RasterizeMs;
                ocrTotal += page.OcrMs;
            }

            if (!vision)
                ner?.Add(page);
        }

        string jobId;
        OcrEntities? distributedEntities = null;
        // Classification streams beside notify so workers can download while pages are sorted.
        // Text layer off keeps the old path: every page is queued before the first claim.
        Func<ClusterJob, CancellationToken, Task>? classify = _config.TextLayerEnabled
            ? (job, token) => StreamTextLayer(pdfBytes, pdfByteCount, pageCount, job, token)
            : null;

        try
        {
            ClusterCoordinator.ClusterRunResult outcome = await _cluster!.RunJobAsync(
                pdfBytes,
                pdfByteCount,
                pageCount,
                dpi,
                sourceUrl,
                async (oneBased, batchId, job, token) =>
                {
                    int[] zeroBased = new int[oneBased.Length];
                    for (int i = 0; i < oneBased.Length; i++)
                        zeroBased[i] = oneBased[i] - 1;
                    await RecognizeIndicesAsync(
                        pdfBytes,
                        pdfByteCount,
                        zeroBased,
                        dpi,
                        page => job.TryAccept(batchId, page),
                        token).ConfigureAwait(false);
                },
                Accept,
                distributeNer,
                textLayerPages: null,
                ct: ct,
                alongside: classify).ConfigureAwait(false);
            jobId = outcome.JobId;
            distributedEntities = outcome.UsedDistributedNer ? outcome.Entities ?? new OcrEntities() : null;
        }
        catch (Exception)
        {
            llmCts.Cancel();
            if (ner is not null)
                await ner.AbandonAsync().ConfigureAwait(false);
            throw;
        }

        for (int i = 0; i < pageCount; i++)
        {
            if (pages[i] is null)
            {
                llmCts.Cancel();
                if (ner is not null)
                    await ner.AbandonAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Missing OCR result for page {i + 1}.");
            }
        }

        string hash = LogTextHash(pages);
        _cluster.PublishLastJobHash(jobId, hash);
        OcrEntities entities = vision
            ? EntitiesFromVisionPages(pages)
            : distributedEntities is not null
                ? distributedEntities
                : await ExtractEntitiesAsync(pages, ner).ConfigureAwait(false);
        List<OcrPageResult> visible = VisiblePages(pages);
        totalSw.Stop();

        OcrResponse response = new()
        {
            Ok = true,
            PageCount = pageCount,
            PdfBytes = pdfByteCount,
            DownloadMode = downloadMode,
            Dpi = dpi,
            Timings = new OcrTimings
            {
                DownloadMs = Math.Round(downloadMs, 1),
                RasterizeMs = Math.Round(rasterTotal, 1),
                OcrMs = Math.Round(ocrTotal, 1),
                TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            },
            Pages = visible,
            Entities = entities,
        };
        StampSources(response, pages);
        return response;
    }

    /// <summary>
    /// Raster + OCR a specific set of 0-based page indices. Used by cluster workers and
    /// by the coordinator's local share. Does not run NER and does not distribute further.
    /// </summary>
    public async Task RecognizeIndicesAsync(
        byte[] pdfBytes,
        int pdfLength,
        IReadOnlyList<int> pageIndices,
        int dpi,
        Action<OcrPageResult> onPage,
        CancellationToken ct)
    {
        if (pageIndices.Count == 0)
            return;
        dpi = Math.Clamp(dpi, 36, 300);
        if (_config.IsLlmMode)
        {
            await RecognizeIndicesVisionAsync(pdfBytes, pdfLength, pageIndices, dpi, onPage, ct)
                .ConfigureAwait(false);
            return;
        }

        int workers;
        Func<SKBitmap, CancellationToken, Task<string>> recognize;
        if (_config.IsWeChatMode)
        {
            if (_wechat is null || !_wechat.IsReady)
                throw new InvalidOperationException("ocr.mode=wechat requires a connected WeChat OCR engine.");
            workers = Math.Max(1, _wechat.InstanceCount);
            recognize = (bitmap, token) => _wechat.RecognizeBitmapAsync(bitmap, token);
        }
        else
        {
            if (_engine is null)
                throw new InvalidOperationException("ocr.mode=local requires OcrEngine.");
            workers = Math.Max(1, _engine.EngineCount);
            recognize = RecognizeLocalAsync;
        }

        RenderOptions renderOptions = CreateRenderOptions(dpi);
        int[] indices = pageIndices as int[] ?? pageIndices.ToArray();
        Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
            Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(Math.Max(4, workers * 2))
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        Task producer = ProduceIndicesAsync(pdfBytes, pdfLength, indices, renderOptions, rasterized.Writer, ct);

        async Task ConsumerAsync()
        {
            await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using (bitmap)
                {
                    Stopwatch ocrSw = Stopwatch.StartNew();
                    int width = bitmap.Width;
                    int height = bitmap.Height;
                    string pageText = await recognize(bitmap, ct).ConfigureAwait(false);
                    ocrSw.Stop();
                    pageText = pageText.Replace("\r", "").Trim();
                    onPage(new OcrPageResult
                    {
                        Page = index + 1,
                        Width = width,
                        Height = height,
                        Text = pageText,
                        Source = PdfTextLayer.SourceOcr,
                        RasterizeMs = Math.Round(rasterMs, 1),
                        OcrMs = Math.Round(ocrSw.Elapsed.TotalMilliseconds, 1),
                    });
                }
            }
        }

        int consumers = Math.Clamp(Math.Min(workers, Math.Max(1, indices.Length)), 1, workers);
        Task[] tasks = new Task[consumers];
        for (int i = 0; i < consumers; i++)
            tasks[i] = ConsumerAsync();

        Exception? failure = null;
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        if (failure is not null)
            throw failure;
    }

    private async Task RecognizeIndicesVisionAsync(
        byte[] pdfBytes,
        int pdfLength,
        IReadOnlyList<int> pageIndices,
        int dpi,
        Action<OcrPageResult> onPage,
        CancellationToken ct)
    {
        if (_vision is null || !_vision.IsUsable)
            throw new InvalidOperationException("ocr.mode=llm requires a usable LLM.");

        RenderOptions renderOptions = CreateRenderOptions(dpi);
        int[] indices = pageIndices as int[] ?? pageIndices.ToArray();
        int jpegQuality = _vision.OcrJpegQuality;
        Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
            Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(Math.Max(4, _pageWindow))
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        Task producer = ProduceIndicesAsync(pdfBytes, pdfLength, indices, renderOptions, rasterized.Writer, ct);
        int concurrency = Math.Clamp(Math.Min(_vision.OcrConcurrency, Math.Max(1, indices.Length)), 1, 64);

        async Task ConsumerAsync()
        {
            await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using (bitmap)
                {
                    byte[] jpeg = LlmVisionOcr.EncodeJpeg(bitmap, jpegQuality);
                    OcrPageResult page = await _vision
                        .RecognizePageAsync(index + 1, bitmap.Width, bitmap.Height, jpeg, rasterMs, ct)
                        .ConfigureAwait(false);
                    onPage(page);
                }
            }
        }

        Task[] tasks = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
            tasks[i] = ConsumerAsync();
        Exception? failure = null;
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        if (failure is not null)
            throw failure;
    }

    private Task ProduceIndicesAsync(
        byte[] pdfBytes,
        int pdfLength,
        int[] pageIndices,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        CancellationToken ct) =>
        DispatchRenderAsync(pdfBytes, pdfLength, pageIndices, renderOptions, writer, ct);

    private string LogTextHash(OcrPageResult?[] pages)
    {
        string hash = OcrTextHash.Compute(pages);
        int nonempty = 0;
        foreach (OcrPageResult? page in pages)
        {
            if (page is not null && ChallengeResultMapper.IncludeInOutput(page))
                nonempty++;
        }

        int textLayer = 0;
        int ocrPages = 0;
        foreach (OcrPageResult? page in pages)
        {
            if (page is null)
                continue;
            if (string.Equals(page.Source, PdfTextLayer.SourceTextLayer, StringComparison.Ordinal))
                textLayer++;
            else
                ocrPages++;
        }

        _logger.LogInformation(
            "OCR_TEXT_SHA256={Hash} pages={Pages} nonempty={NonEmpty} textLayer={TextLayer} ocr={Ocr}",
            hash,
            pages.Length,
            nonempty,
            textLayer,
            ocrPages);
        Console.Out.Flush();
        return hash;
    }

    private static List<OcrPageResult> VisiblePages(OcrPageResult[] pages)
    {
        List<OcrPageResult> visible = new(pages.Length);
        foreach (OcrPageResult page in pages)
        {
            if (page is not null && ChallengeResultMapper.IncludeInOutput(page))
                visible.Add(page);
        }

        return visible;
    }

    private static RenderOptions CreateRenderOptions(int dpi) =>
        new(
            Dpi: dpi,
            WithAnnotations: false,
            WithFormFill: false,
            AntiAliasing: PdfAntiAliasing.None,
            // Grayscale + no tiling makes PDFtoImage.Parallel ship Gray8 and expand
            // back to BGRA8888 in the host. OCR only accepts BGR/RGB(A); that host
            // bitmap is already Bgra8888, so EnsureBgra8888 does not convert again.
            Grayscale: true);

    private static OcrEntities EntitiesFromVisionPages(OcrPageResult[] pages)
    {
        EntityAccumulator acc = new();
        foreach (OcrPageResult page in pages)
        {
            if (page.RuleList is null)
                continue;
            foreach (ChallengeRule rule in page.RuleList)
            {
                string code = (rule.RuleCode ?? "").Trim().ToUpperInvariant();
                foreach (ChallengeRuleItem item in rule.RuleItemList)
                {
                    if (code == "B04" && !string.IsNullOrWhiteSpace(item.PersonName))
                    {
                        int n = Math.Max(1, item.Count);
                        for (int i = 0; i < n; i++)
                            acc.AddPerson(item.PersonName.Trim(), page.Page);
                    }
                    else if (code == "B06" && !string.IsNullOrWhiteSpace(item.CompanyName))
                    {
                        int n = Math.Max(1, item.Count);
                        for (int i = 0; i < n; i++)
                            acc.AddCompany(item.CompanyName.Trim(), page.Page);
                    }
                }
            }
        }

        return EntityExtractor.ToEntities(acc);
    }

    private async Task<OcrEntities> ExtractEntitiesAsync(
        OcrPageResult[] pages,
        LlmEntityExtractor.LlmExtractionSession? ner)
    {
        // Heuristics only when LLM was never attempted (disabled / no key).
        // After an LLM NER attempt, never fall back — log and return empty.
        bool fallbackWhenNoLlm = _llm?.Config.FallbackToHeuristics ?? false;

        if (ner is not null)
        {
            try
            {
                OcrEntities llmEntities = await ner.CompleteAsync().ConfigureAwait(false);
                _logger.LogInformation(
                    "LLM NER done: companies={Companies}, persons={Persons}",
                    llmEntities.Companies.Count,
                    llmEntities.Persons.Count);
                return llmEntities;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "LLM NER failed; returning empty entities (no heuristic fallback after LLM)");
                return new OcrEntities();
            }
        }

        if (fallbackWhenNoLlm)
        {
            List<string> texts = pages.Select(p => p.Text ?? "").ToList();
            return EntityExtractor.ExtractFromPages(texts);
        }

        _logger.LogInformation(
            "LLM NER unused/unconfigured and fallbackToHeuristics=false — empty entities");
        return new OcrEntities();
    }

    private sealed class TextLayerPlan
    {
        public OcrPageResult?[] Prepared { get; init; } = [];
        public int[] OcrIndices { get; init; } = [];
        public double AnalyzeMs { get; init; }
    }

    /// <summary>
    /// Read every page's text layer once. Usable pages become OCR results immediately;
    /// the rest are the only indices sent to the raster pipeline.
    /// </summary>
    private TextLayerPlan PlanTextLayer(byte[] pdfBytes, int pdfLength, int pageCount)
    {
        int[] every = new int[Math.Max(0, pageCount)];
        for (int i = 0; i < every.Length; i++)
            every[i] = i;
        if (!_config.TextLayerEnabled || pageCount <= 0)
            return new TextLayerPlan { Prepared = new OcrPageResult?[pageCount], OcrIndices = every };

        PdfTextLayer.Thresholds thresholds = new(
            _config.TextLayerMinChars,
            _config.TextLayerMaxUnknownRatio,
            _config.TextLayerImageCoverage,
            _config.TextLayerImageMinChars);
        OcrPageResult?[] prepared = new OcrPageResult?[pageCount];
        List<int> ocr = new(pageCount);
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            using MemoryStream stream = new(pdfBytes, 0, pdfLength, writable: false, publiclyVisible: true);
            using PdfSession session = PdfSession.Open(stream, leaveOpen: true);
            int limit = Math.Min(pageCount, session.PageCount);
            for (int i = 0; i < limit; i++)
            {
                PdfPageAnalysis analysis;
                try
                {
                    analysis = session.AnalyzePage(i);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Text layer analysis failed for page {Page}; it will be OCR'd", i + 1);
                    ocr.Add(i);
                    continue;
                }

                PdfTextLayer.Decision decision = PdfTextLayer.Classify(_config.TextLayer, analysis, thresholds);
                if (!decision.Use)
                {
                    _logger.LogDebug(
                        "Text layer page {Page}/{Total} source=ocr reason={Reason} chars={Chars} unknown={Unknown} coverage={Coverage:F3}",
                        i + 1,
                        pageCount,
                        decision.Reason,
                        analysis.Text.CharacterCount,
                        analysis.Text.UnknownCharacterCount,
                        analysis.Content.ImageAreaCoverage);
                    ocr.Add(i);
                    continue;
                }

                string text = PdfTextLayer.Normalize(analysis.Text.Text);
                prepared[i] = new OcrPageResult
                {
                    Page = i + 1,
                    Text = text,
                    Source = PdfTextLayer.SourceTextLayer,
                };
                _logger.LogInformation(
                    "Text layer page {Page}/{Total} source=textLayer reason={Reason} chars={Chars} unknown={Unknown} textObjects={Objects} invisible={Invisible} coverage={Coverage:F3}",
                    i + 1,
                    pageCount,
                    decision.Reason,
                    PdfTextLayer.CountNonWhitespace(text),
                    analysis.Text.UnknownCharacterCount,
                    analysis.Content.TextObjectCount,
                    analysis.Content.TextObjectsAreInvisible,
                    analysis.Content.ImageAreaCoverage);
            }

            for (int i = limit; i < pageCount; i++)
                ocr.Add(i);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text layer analysis failed; rendering every page");
            return new TextLayerPlan
            {
                Prepared = new OcrPageResult?[pageCount],
                OcrIndices = every,
                AnalyzeMs = sw.Elapsed.TotalMilliseconds,
            };
        }

        sw.Stop();
        int textCount = 0;
        foreach (OcrPageResult? page in prepared)
        {
            if (page is not null)
                textCount++;
        }

        _logger.LogInformation(
            "Text layer summary: mode={Mode} textLayer={TextLayer} ocr={Ocr} analyzeMs={Analyze:F1}",
            _config.TextLayer,
            textCount,
            ocr.Count,
            sw.Elapsed.TotalMilliseconds);
        return new TextLayerPlan
        {
            Prepared = prepared,
            OcrIndices = ocr.ToArray(),
            AnalyzeMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// Classify pages on a side thread and publish each decision as it is known.
    /// A text-layer page is completed once; anything else is admitted for OCR.
    /// On failure or cancel, whatever is still invisible is admitted so the job cannot stall.
    /// </summary>
    private Task StreamTextLayer(
        byte[] pdfBytes,
        int pdfLength,
        int pageCount,
        ClusterJob job,
        CancellationToken ct)
    {
        int next = 0;
        int textCount = 0;
        int ocrCount = 0;
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            PdfTextLayer.Thresholds thresholds = new(
                _config.TextLayerMinChars,
                _config.TextLayerMaxUnknownRatio,
                _config.TextLayerImageCoverage,
                _config.TextLayerImageMinChars);
            using MemoryStream stream = new(pdfBytes, 0, pdfLength, writable: false, publiclyVisible: true);
            using PdfSession session = PdfSession.Open(stream, leaveOpen: true);
            int limit = Math.Min(pageCount, session.PageCount);
            for (; next < limit; next++)
            {
                ct.ThrowIfCancellationRequested();
                PdfPageAnalysis analysis;
                try
                {
                    analysis = session.AnalyzePage(next);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Text layer analysis failed for page {Page}; it will be OCR'd", next + 1);
                    if (job.Scheduler.AdmitPage(next))
                        ocrCount++;
                    continue;
                }

                PdfTextLayer.Decision decision = PdfTextLayer.Classify(_config.TextLayer, analysis, thresholds);
                if (!decision.Use)
                {
                    _logger.LogDebug(
                        "Text layer page {Page}/{Total} source=ocr reason={Reason} chars={Chars} unknown={Unknown} coverage={Coverage:F3}",
                        next + 1,
                        pageCount,
                        decision.Reason,
                        analysis.Text.CharacterCount,
                        analysis.Text.UnknownCharacterCount,
                        analysis.Content.ImageAreaCoverage);
                    if (job.Scheduler.AdmitPage(next))
                        ocrCount++;
                    continue;
                }

                string text = PdfTextLayer.Normalize(analysis.Text.Text);
                if (!job.Scheduler.CompleteWithoutOcr(next))
                {
                    if (job.Scheduler.AdmitPage(next))
                        ocrCount++;
                    continue;
                }

                textCount++;
                job.AcceptPrepared(new OcrPageResult
                {
                    Page = next + 1,
                    Text = text,
                    Source = PdfTextLayer.SourceTextLayer,
                });
                _logger.LogInformation(
                    "Text layer page {Page}/{Total} source=textLayer reason={Reason} chars={Chars} unknown={Unknown} textObjects={Objects} invisible={Invisible} coverage={Coverage:F3}",
                    next + 1,
                    pageCount,
                    decision.Reason,
                    PdfTextLayer.CountNonWhitespace(text),
                    analysis.Text.UnknownCharacterCount,
                    analysis.Content.TextObjectCount,
                    analysis.Content.TextObjectsAreInvisible,
                    analysis.Content.ImageAreaCoverage);
            }

            for (; next < pageCount; next++)
            {
                if (job.Scheduler.AdmitPage(next))
                    ocrCount++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Text layer analysis failed; remaining pages will be OCR'd");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Text layer analysis cancelled; remaining pages will be OCR'd");
        }
        finally
        {
            for (int i = next; i < pageCount; i++)
            {
                if (job.Scheduler.AdmitPage(i))
                    ocrCount++;
            }

            sw.Stop();
            _logger.LogInformation(
                "Text layer summary: mode={Mode} textLayer={TextLayer} ocr={Ocr} analyzeMs={Analyze:F1}",
                _config.TextLayer,
                textCount,
                ocrCount,
                sw.Elapsed.TotalMilliseconds);
        }

        return Task.CompletedTask;
    }

    private static void PlacePrepared(
        OcrPageResult[] pages,
        TextLayerPlan plan,
        LlmEntityExtractor.LlmExtractionSession? ner)
    {
        int n = Math.Min(pages.Length, plan.Prepared.Length);
        for (int i = 0; i < n; i++)
        {
            if (plan.Prepared[i] is not { } page)
                continue;
            pages[i] = page;
            ner?.Add(page);
        }
    }

    private void StampSources(OcrResponse response, OcrPageResult?[] pages)
    {
        int text = 0;
        int ocr = 0;
        foreach (OcrPageResult? page in pages)
        {
            if (page is null)
                continue;
            if (string.Equals(page.Source, PdfTextLayer.SourceTextLayer, StringComparison.Ordinal))
                text++;
            else
                ocr++;
        }

        response.TextLayerMode = _config.TextLayer;
        response.TextLayerPageCount = text;
        response.OcrPageCount = ocr;
    }

    private Task ProduceParallelAsync(
        byte[] pdfBytes,
        int pdfLength,
        int pageCount,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        CancellationToken ct)
    {
        int[] pageIndices = new int[pageCount];
        for (int i = 0; i < pageCount; i++)
            pageIndices[i] = i;
        return DispatchRenderAsync(pdfBytes, pdfLength, pageIndices, renderOptions, writer, ct);
    }

    /// <summary>
    /// In-process PDFium calls share one global lock, so extra threads do not
    /// rasterize in parallel. <c>ocr.renderMode=parallel</c> renders in worker
    /// processes that share one memory-mapped temp PDF (Gray8 on the wire when
    /// <see cref="CreateRenderOptions"/> asks for grayscale). A worker crash
    /// drops the pool and finishes the unwritten pages in-process. Cancellation
    /// does not fall back.
    /// </summary>
    private async Task DispatchRenderAsync(
        byte[] pdfBytes,
        int pdfLength,
        int[] pageIndices,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        CancellationToken ct)
    {
        Stopwatch wall = Stopwatch.StartNew();
        string mode = _config.IsParallelRender ? "parallel" : "inprocess";
        int workers = _config.IsParallelRender
            ? _config.RenderProcessCount
            : Math.Clamp(Math.Min(_rasterWorkers, Math.Max(1, pageIndices.Length)), 1, 8);
        HashSet<int> written = new();
        try
        {
            if (_config.IsParallelRender && pageIndices.Length > 0)
            {
                try
                {
                    await ProduceWithParallelAsync(
                        pdfBytes, pdfLength, pageIndices, renderOptions, writer, written, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Parallel PDF render failed after {Written} pages; falling back to in-process for the rest",
                        written.Count);
                    AbandonParallel();
                    mode = "parallel-fallback-inprocess";
                    int[] remaining = RemainingIndices(pageIndices, written);
                    workers = Math.Clamp(Math.Min(_rasterWorkers, Math.Max(1, remaining.Length)), 1, 8);
                    if (remaining.Length > 0)
                    {
                        await ProduceInProcessAsync(
                            pdfBytes, pdfLength, remaining, renderOptions, writer, ct)
                            .ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await ProduceInProcessAsync(
                    pdfBytes, pdfLength, pageIndices, renderOptions, writer, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            wall.Stop();
            _logger.LogInformation(
                "Raster produce mode={Mode} workers={Workers} pages={Pages} wallMs={WallMs:F1}",
                mode,
                workers,
                pageIndices.Length,
                wall.Elapsed.TotalMilliseconds);
            writer.TryComplete();
        }
    }

    private async Task ProduceWithParallelAsync(
        byte[] pdfBytes,
        int pdfLength,
        int[] pageIndices,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        HashSet<int> written,
        CancellationToken ct)
    {
        // One temp file per PDF per process. PDFtoImage.Parallel reopens that path
        // (ReuseFileStream + TryOpenSourceFile) for every batch; leaveOpen keeps our handle.
        MappedPdf mapped = await RetainMappedAsync(pdfBytes, pdfLength, ct).ConfigureAwait(false);
        try
        {
            FileStream stream = mapped.Stream
                ?? throw new InvalidOperationException("Mapped PDF was closed before render.");
            ParallelPdfProcessor processor = GetOrCreateParallel();
            Stopwatch sw = Stopwatch.StartNew();
            int idx = 0;
            await foreach (SKBitmap bitmap in processor.ToImagesAsync(
                stream, pageIndices, leaveOpen: true, options: renderOptions, cancellationToken: ct)
                .ConfigureAwait(false))
            {
                sw.Stop();
                if (idx >= pageIndices.Length)
                {
                    bitmap.Dispose();
                    throw new InvalidOperationException(
                        $"PDFtoImage.Parallel yielded more than {pageIndices.Length} pages.");
                }

                int pageIndex = pageIndices[idx++];
                try
                {
                    await writer.WriteAsync((pageIndex, bitmap, sw.Elapsed.TotalMilliseconds), ct)
                        .ConfigureAwait(false);
                    written.Add(pageIndex);
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }

                sw.Restart();
            }

            if (idx != pageIndices.Length)
            {
                throw new InvalidOperationException(
                    $"PDFtoImage.Parallel yielded {idx} pages, expected {pageIndices.Length}.");
            }
        }
        finally
        {
            await EndMappedUseAsync(mapped).ConfigureAwait(false);
        }
    }

    private async Task ProduceInProcessAsync(
        byte[] pdfBytes,
        int pdfLength,
        int[] pageIndices,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        CancellationToken ct)
    {
        if (pageIndices.Length == 0)
            return;

        int workers = Math.Clamp(Math.Min(_rasterWorkers, Math.Max(1, pageIndices.Length)), 1, 8);
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            int workerId = w;
            tasks[w] = Task.Run(async () =>
            {
                using MemoryStream local = new(
                    pdfBytes, index: 0, count: pdfLength, writable: false, publiclyVisible: true);
                List<int> mine = new((pageIndices.Length + workers - 1) / workers);
                for (int i = workerId; i < pageIndices.Length; i += workers)
                    mine.Add(pageIndices[i]);
                if (mine.Count == 0)
                    return;

                IEnumerator<SKBitmap> enumerator =
                    Conversion.ToImages(local, mine, leaveOpen: true, options: renderOptions)
                        .GetEnumerator();
                try
                {
                    int idx = 0;
                    while (idx < mine.Count)
                    {
                        ct.ThrowIfCancellationRequested();
                        Stopwatch sw = Stopwatch.StartNew();
                        if (!enumerator.MoveNext())
                        {
                            throw new InvalidOperationException(
                                $"PDFtoImage yielded {idx} pages, expected {mine.Count}.");
                        }

                        SKBitmap bitmap = enumerator.Current;
                        sw.Stop();
                        int pageIndex = mine[idx++];
                        try
                        {
                            await writer.WriteAsync((pageIndex, bitmap, sw.Elapsed.TotalMilliseconds), ct)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            bitmap.Dispose();
                            while (enumerator.MoveNext())
                                enumerator.Current.Dispose();
                            throw;
                        }
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private ParallelPdfProcessor GetOrCreateParallel()
    {
        lock (_parallelGate)
        {
            ObjectDisposedException.ThrowIf(_parallelDisposed != 0, this);
            return _parallel ??= new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = Math.Clamp(_config.RenderProcessCount, 1, 8),
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                ReuseFileStream = true,
            });
        }
    }

    private void AbandonParallel()
    {
        ParallelPdfProcessor? processor;
        lock (_parallelGate)
        {
            processor = _parallel;
            _parallel = null;
        }

        if (processor is null)
            return;
        try
        {
            processor.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing failed parallel PDF renderer");
        }
    }

    /// <summary>
    /// Drop the temp PDF kept for <paramref name="pdf"/>. Called when the job or worker
    /// session that owns this buffer is finished. In-flight batches keep the file until
    /// they return it.
    /// </summary>
    public void ReleaseMappedPdf(byte[] pdf)
    {
        _mapGate.Wait();
        try
        {
            for (int i = _mapped.Count - 1; i >= 0; i--)
            {
                MappedPdf mapped = _mapped[i];
                if (!ReferenceEquals(mapped.Bytes, pdf))
                    continue;
                mapped.ReleaseRequested = true;
                if (mapped.Uses == 0)
                    CloseMapped(mapped);
            }
        }
        finally
        {
            _mapGate.Release();
        }
    }

    private async Task<MappedPdf> RetainMappedAsync(byte[] pdfBytes, int pdfLength, CancellationToken ct)
    {
        await _mapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int i = 0; i < _mapped.Count; i++)
            {
                MappedPdf existing = _mapped[i];
                if (!ReferenceEquals(existing.Bytes, pdfBytes) || existing.Length != pdfLength || existing.Stream is null)
                    continue;
                existing.Uses++;
                _logger.LogDebug(
                    "Parallel render reusing mapped PDF bytes={Bytes} uses={Uses}",
                    pdfLength,
                    existing.Uses);
                return existing;
            }

            string path = Path.Combine(Path.GetTempPath(), "miniocr-render-" + Guid.NewGuid().ToString("N") + ".pdf");
            FileStream? stream = null;
            try
            {
                await WritePdfTempAsync(path, pdfBytes, pdfLength, ct).ConfigureAwait(false);
                // Share.Read|Delete: the processor reopens by path, and we can unlink at job end.
                // Not DeleteOnClose: a later batch must still see the file.
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            catch
            {
                if (stream is not null)
                {
                    try
                    {
                        stream.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to close partial mapped PDF {Path}", path);
                    }
                }

                TryDeleteTemp(path);
                throw;
            }

            var created = new MappedPdf
            {
                Bytes = pdfBytes,
                Length = pdfLength,
                Path = path,
                Stream = stream,
                Uses = 1,
            };
            _mapped.Add(created);
            _logger.LogInformation("Parallel render mapped PDF once bytes={Bytes}", pdfLength);
            return created;
        }
        finally
        {
            _mapGate.Release();
        }
    }

    private async Task EndMappedUseAsync(MappedPdf mapped)
    {
        await _mapGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (mapped.Uses > 0)
                mapped.Uses--;
            if (mapped.Uses == 0 && mapped.ReleaseRequested)
                CloseMapped(mapped);
        }
        finally
        {
            _mapGate.Release();
        }
    }

    private void CloseMapped(MappedPdf mapped)
    {
        FileStream? stream = mapped.Stream;
        string path = mapped.Path;
        mapped.Stream = null;
        mapped.Bytes = [];
        _mapped.Remove(mapped);
        if (stream is not null)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close mapped PDF {Path}", path);
            }
        }

        TryDeleteTemp(path);
    }

    private void ReleaseAllMapped()
    {
        _mapGate.Wait();
        try
        {
            for (int i = _mapped.Count - 1; i >= 0; i--)
            {
                MappedPdf mapped = _mapped[i];
                mapped.ReleaseRequested = true;
                mapped.Uses = 0;
                CloseMapped(mapped);
            }
        }
        finally
        {
            _mapGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _parallelDisposed, 1) != 0)
            return;

        ReleaseAllMapped();

        ParallelPdfProcessor? processor;
        lock (_parallelGate)
        {
            processor = _parallel;
            _parallel = null;
        }

        if (processor is null)
            return;
        try
        {
            await processor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose parallel PDF renderer");
        }
    }

    private static int[] RemainingIndices(int[] pageIndices, HashSet<int> written)
    {
        if (written.Count == 0)
            return pageIndices;
        int[] remaining = new int[pageIndices.Length - written.Count];
        int n = 0;
        foreach (int index in pageIndices)
        {
            if (!written.Contains(index))
                remaining[n++] = index;
        }

        if (n == remaining.Length)
            return remaining;
        return remaining[..n];
    }

    private static async Task WritePdfTempAsync(string path, byte[] pdfBytes, int pdfLength, CancellationToken ct)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1024 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using FileStream output = new(path, options);
        await output.WriteAsync(pdfBytes.AsMemory(0, pdfLength), ct).ConfigureAwait(false);
    }

    private void TryDeleteTemp(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temporary PDF {Path}", path);
                return;
            }
        }
    }

    private static void EnsureBgra8888(SKBitmap source, out SKBitmap working, out bool owned)
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

    private readonly record struct PageJpeg(int Index, int Width, int Height, byte[]? Jpeg, double RasterMs);

    /// <summary>
    /// One temp PDF shared by every parallel-render batch of the same buffer.
    /// Keyed by array reference because rented buffers return to the pool after the job.
    /// </summary>
    private sealed class MappedPdf
    {
        public byte[] Bytes = [];
        public int Length;
        public string Path = "";
        public FileStream? Stream;
        public int Uses;
        public bool ReleaseRequested;
    }
}
