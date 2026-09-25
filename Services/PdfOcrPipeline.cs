using System.Diagnostics;
using System.Threading.Channels;
using MiniOcr.Models;
using PDFtoImage;
using Sdcb.SimdPaddleOCR;
using SkiaSharp;

namespace MiniOcr.Services;

public sealed class PdfOcrPipeline
{
    private readonly OcrEngine? _engine;
    private readonly ILogger<PdfOcrPipeline> _logger;
    private readonly LlmEntityExtractor? _llm;
    private readonly LlmVisionOcr? _vision;
    private readonly HunyuanVisionOcr? _hunyuan;
    private readonly OcrRuntimeConfig _config;
    private readonly int _pageWindow;
    private readonly int _defaultDpi;
    private readonly int _rasterWorkers;

    public PdfOcrPipeline(
        OcrRuntimeConfig config,
        ILogger<PdfOcrPipeline> logger,
        OcrEngine? engine = null,
        LlmEntityExtractor? llm = null,
        LlmVisionOcr? vision = null,
        HunyuanVisionOcr? hunyuan = null)
    {
        _config = config;
        _logger = logger;
        _engine = engine;
        _llm = llm;
        _vision = vision;
        _hunyuan = hunyuan;
        _defaultDpi = config.DefaultDpi;
        _rasterWorkers = config.RasterWorkerCount;

        if (config.IsLlmMode)
        {
            if (vision is null || !vision.IsUsable)
                throw new InvalidOperationException(
                    "ocr.mode=llm requires a usable LLM (enabled + apiKey + baseUrl + model).");
            _pageWindow = Math.Max(4, Math.Min(vision.OcrConcurrency, 64));
        }
        else if (config.IsHunyuanMode)
        {
            if (hunyuan is null || !hunyuan.IsUsable)
                throw new InvalidOperationException(
                    "ocr.mode=hunyuan requires hunyuan.enabled + baseUrl + model (local OpenAI-compatible server).");
            _pageWindow = Math.Max(4, Math.Min(hunyuan.Concurrency, 64));
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
        int? dpiOverride = null)
    {
        Stopwatch totalSw = Stopwatch.StartNew();
        byte[] array = pdf.DangerousGetArray();
        int length = pdf.Length;
        return ProcessCoreAsync(array, length, downloadMs, downloadMode, totalSw, dpiOverride, ct);
    }

    private async Task<OcrResponse> ProcessCoreAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        int? dpiOverride,
        CancellationToken ct)
    {
        int pageCount;
        using (MemoryStream countStream = new(pdfBytes, index: 0, count: pdfByteCount, writable: false, publiclyVisible: true))
            pageCount = Conversion.GetPageCount(countStream, leaveOpen: false);

        if (pageCount <= 0)
            throw new InvalidOperationException("PDF has no pages.");
        if (pageCount > 2000)
            throw new InvalidOperationException($"PDF has {pageCount} pages; max supported is 2000.");

        int dpi = Math.Clamp(dpiOverride ?? _defaultDpi, 36, 300);

        if (_config.IsLlmMode)
        {
            _logger.LogInformation(
                "OCR pipeline (llm vision): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, ocrConcurrency={Conc}, rasterWorkers={Raster}",
                pageCount, pdfByteCount, dpi, _vision!.OcrConcurrency, _rasterWorkers);
            return await ProcessWithVisionAsync(
                pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct)
                .ConfigureAwait(false);
        }

        if (_config.IsHunyuanMode)
        {
            _logger.LogInformation(
                "OCR pipeline (hunyuan): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, concurrency={Conc}, rasterWorkers={Raster}, model={Model}",
                pageCount, pdfByteCount, dpi, _hunyuan!.Concurrency, _rasterWorkers, _hunyuan.Config.Model);
            return await ProcessWithHunyuanAsync(
                pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "OCR pipeline (local): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, engines={Engines}, rasterWorkers={Raster}",
            pageCount, pdfByteCount, dpi, _engine!.EngineCount, _rasterWorkers);

        return await ProcessWithLocalAsync(
            pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct)
            .ConfigureAwait(false);
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

        Task producer = ProduceParallelAsync(
            pdfBytes, pdfByteCount, pageCount, renderOptions, rasterized.Writer, ct);

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

        totalSw.Stop();

        _logger.LogInformation(
            "Vision pipeline done: firstJpegLogged={FirstJpeg}, firstVisionLogged={FirstVision}, " +
            "totalMs={Total:F1}",
            firstJpegFlag == 1,
            firstVisionFlag == 1,
            totalSw.Elapsed.TotalMilliseconds);

        return new OcrResponse
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
            Pages = pages.ToList(),
            Entities = entities,
        };
    }

    private async Task<OcrResponse> ProcessWithHunyuanAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        int pageCount,
        int dpi,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        // Color raster: HunyuanOCR is trained on document images, not the grayscale
        // pages used by Paddle / the cloud-vision path.
        RenderOptions renderOptions = CreateRenderOptions(dpi, grayscale: false);
        int concurrency = _hunyuan!.Concurrency;
        int jpegQuality = _hunyuan.JpegQuality;

        Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
            Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(_pageWindow)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

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
        int firstRequestFlag = 0;

        Stopwatch sinceDownload = Stopwatch.StartNew();
        _logger.LogInformation(
            "Hunyuan pipeline: download done (mode={Mode}, downloadMs={DownloadMs:F1}); " +
            "starting overlapped raster+encode→server; pages={Pages}, dpi={Dpi}, " +
            "jpegQuality={JpegQ}, rasterWorkers={Raster}, concurrency={Conc}, " +
            "jpegQueueCapacity={JpegCap}, maxTokens={MaxTokens}",
            downloadMode,
            downloadMs,
            pageCount,
            dpi,
            jpegQuality,
            _rasterWorkers,
            concurrency,
            jpegCapacity,
            _hunyuan.Config.MaxTokens);

        Task producer = ProduceParallelAsync(
            pdfBytes, pdfByteCount, pageCount, renderOptions, rasterized.Writer, ct);

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
                            "Hunyuan pipeline: first page JPEG ready (page={Page}, {W}x{H}, jpegBytes={Bytes}) " +
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

        async Task HunyuanConsumerAsync()
        {
            await foreach (PageJpeg img in jpegs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (img.Jpeg is null)
                    continue;

                if (Interlocked.CompareExchange(ref firstRequestFlag, 1, 0) == 0)
                {
                    _logger.LogInformation(
                        "Hunyuan pipeline: first request (page={Page}) " +
                        "at t+{Elapsed:F0}ms after download handoff",
                        img.Index + 1,
                        sinceDownload.Elapsed.TotalMilliseconds);
                }

                OcrPageResult page = await _hunyuan
                    .RecognizePageAsync(img.Index + 1, img.Width, img.Height, img.Jpeg, img.RasterMs, ct)
                    .ConfigureAwait(false);
                pages[img.Index] = page;
                lock (timingLock)
                    ocrTotal += page.OcrMs;
            }
        }

        int encodeWorkers = Math.Clamp(_rasterWorkers, 1, 8);
        Task[] encoders = Enumerable.Range(0, encodeWorkers)
            .Select(_ => EncodeConsumerAsync())
            .ToArray();

        // A dead server must not stall the raster/encode side on a full channel.
        Exception? hunyuanFailed = null;
        async Task HunyuanGuardedAsync()
        {
            try
            {
                await HunyuanConsumerAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref hunyuanFailed, ex, null);
                jpegs.Writer.TryComplete(ex);
                rasterized.Writer.TryComplete(ex);
                throw;
            }
        }

        int hunyuanWorkers = Math.Clamp(concurrency, 1, Math.Max(1, pageCount));
        Task[] hunyuanTasks = Enumerable.Range(0, hunyuanWorkers)
            .Select(_ => HunyuanGuardedAsync())
            .ToArray();

        // Close the JPEG channel only after encode finishes, so consumers' ReadAllAsync can end.
        // Completing it earlier deadlocks the success path; completing it only after consumers
        // also deadlocks, because they wait for the writer. A failed request completes both
        // writers immediately inside HunyuanGuardedAsync.
        Task encodeAndProduce = Task.WhenAll(producer, Task.WhenAll(encoders));
        _ = encodeAndProduce.ContinueWith(
            t =>
            {
                Exception? encodeError = t.Exception?.InnerException;
                jpegs.Writer.TryComplete(encodeError);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        try
        {
            await Task.WhenAll(encodeAndProduce, Task.WhenAll(hunyuanTasks)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (hunyuanFailed is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(hunyuanFailed).Throw();
            throw;
        }
        finally
        {
            jpegs.Writer.TryComplete();
            rasterized.Writer.TryComplete();
            while (rasterized.Reader.TryRead(out var leftover))
                leftover.Bitmap.Dispose();
        }

        for (int i = 0; i < pageCount; i++)
        {
            if (pages[i] is null)
                throw new InvalidOperationException($"Missing Hunyuan OCR result for page index {i}.");
        }

        // Page text only. B04/B06 come from the same NER path as local Paddle.
        OcrEntities entities = await ExtractEntitiesAsync(pages, ct).ConfigureAwait(false);

        totalSw.Stop();

        _logger.LogInformation(
            "Hunyuan pipeline done: totalMs={Total:F1}, companies={Companies}, persons={Persons}",
            totalSw.Elapsed.TotalMilliseconds,
            entities.Companies.Count,
            entities.Persons.Count);

        return new OcrResponse
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
            Pages = pages.ToList(),
            Entities = entities,
        };
    }

    private async Task<OcrResponse> ProcessWithLocalAsync(
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

        Task producer = ProduceParallelAsync(
            pdfBytes, pdfByteCount, pageCount, renderOptions, rasterized.Writer, ct);

        async Task ConsumerAsync()
        {
            await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct)
                               .ConfigureAwait(false))
            {
                using (bitmap)
                {
                    EnsureBgra8888(bitmap, out SKBitmap working, out bool ownedWorking);
                    try
                    {
                        Stopwatch ocrSw = Stopwatch.StartNew();
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
                        ocrSw.Stop();

                        string pageText = result.Text?.Replace("\r", "").Trim() ?? "";
                        pages[index] = new OcrPageResult
                        {
                            Page = index + 1,
                            Width = width,
                            Height = height,
                            Text = pageText,
                            RasterizeMs = Math.Round(rasterMs, 1),
                            OcrMs = Math.Round(ocrSw.Elapsed.TotalMilliseconds, 1),
                        };

                        lock (timingLock)
                        {
                            rasterTotal += rasterMs;
                            ocrTotal += ocrSw.Elapsed.TotalMilliseconds;
                        }
                    }
                    finally
                    {
                        if (ownedWorking)
                            working.Dispose();
                    }
                }
            }
        }

        Task[] consumers = Enumerable.Range(0, _engine!.EngineCount)
            .Select(_ => ConsumerAsync())
            .ToArray();

        await producer.ConfigureAwait(false);
        await Task.WhenAll(consumers).ConfigureAwait(false);

        OcrEntities entities = await ExtractEntitiesAsync(pages, ct).ConfigureAwait(false);

        totalSw.Stop();

        return new OcrResponse
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
            Pages = pages.ToList(),
            Entities = entities,
        };
    }

    private static RenderOptions CreateRenderOptions(int dpi, bool grayscale = true) =>
        new(
            Dpi: dpi,
            WithAnnotations: false,
            WithFormFill: false,
            AntiAliasing: PdfAntiAliasing.None,
            Grayscale: grayscale);

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

    private async Task<OcrEntities> ExtractEntitiesAsync(OcrPageResult[] pages, CancellationToken ct)
    {
        bool preferLlm = _llm is { IsUsable: true };
        // Heuristics only when LLM was never attempted (disabled / no key).
        // After an LLM NER attempt, never fall back — log and return empty.
        bool fallbackWhenNoLlm = _llm?.Config.FallbackToHeuristics ?? false;

        if (preferLlm)
        {
            try
            {
                OcrEntities llmEntities = await _llm!.ExtractAsync(pages, ct).ConfigureAwait(false);
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

    private async Task ProduceParallelAsync(
        byte[] pdfBytes,
        int pdfLength,
        int pageCount,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        CancellationToken ct)
    {
        int workers = Math.Clamp(_rasterWorkers, 1, Math.Max(1, pageCount));
        try
        {
            Task[] tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                int workerId = w;
                tasks[w] = Task.Run(async () =>
                {
                    using MemoryStream local = new(
                        pdfBytes, index: 0, count: pdfLength, writable: false, publiclyVisible: true);

                    List<int> pageIndices = new((pageCount + workers - 1) / workers);
                    for (int i = workerId; i < pageCount; i += workers)
                        pageIndices.Add(i);

                    IEnumerator<SKBitmap> enumerator =
                        Conversion.ToImages(local, pageIndices, leaveOpen: true, options: renderOptions)
                            .GetEnumerator();
                    try
                    {
                        int idx = 0;
                        while (idx < pageIndices.Count)
                        {
                            ct.ThrowIfCancellationRequested();
                            Stopwatch sw = Stopwatch.StartNew();
                            if (!enumerator.MoveNext())
                            {
                                throw new InvalidOperationException(
                                    $"PDFtoImage yielded {idx} pages, expected {pageIndices.Count}.");
                            }

                            SKBitmap bitmap = enumerator.Current;
                            sw.Stop();
                            int pageIndex = pageIndices[idx++];
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
        finally
        {
            writer.TryComplete();
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
}
