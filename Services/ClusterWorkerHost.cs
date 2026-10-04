using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using MiniOcr;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Worker side of the cluster. Registers with the coordinator, pulls each PDF once,
/// then pulls page batches until the job is done. OCR uses this process's own engine.
/// </summary>
public sealed class ClusterWorkerHost : IHostedService
{
    private readonly ClusterRuntimeConfig _config;
    private readonly ClusterSelf _self;
    private readonly PdfOcrPipeline _pipeline;
    private readonly LlmEntityExtractor _llm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ParallelPdfDownloader _downloader;
    private readonly ILogger<ClusterWorkerHost> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _pdfCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<byte[]>> _urlFetches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _recentlyLeft = new(StringComparer.Ordinal);
    private readonly object _urlFetchOrderGate = new();
    private readonly List<string> _urlFetchOrder = [];
    private readonly object _pdfCacheOrder = new();
    private readonly List<string> _pdfCacheKeys = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _pagesDone;
    private int _inFlight;

    public ClusterWorkerHost(
        ClusterRuntimeConfig config,
        ClusterSelf self,
        PdfOcrPipeline pipeline,
        LlmEntityExtractor llm,
        IHttpClientFactory httpFactory,
        ParallelPdfDownloader downloader,
        ILogger<ClusterWorkerHost> logger)
    {
        _config = config;
        _self = self;
        _pipeline = pipeline;
        _llm = llm;
        _httpFactory = httpFactory;
        _downloader = downloader;
        _logger = logger;
    }

    private const int MaxCachedPdfs = 2;
    private static readonly TimeSpan RecentLeaveCooldown = TimeSpan.FromSeconds(45);

    private bool RunsDistributedNer => _config.DistributedNer && _llm.IsUsable;

    private int AdvertisedNerConcurrency =>
        RunsDistributedNer ? Math.Clamp(_llm.Config.MaxConcurrency, 1, 32) : 0;

    public int ActiveSessions => _sessions.Count;
    public int PagesDone => Volatile.Read(ref _pagesDone);

    public ClusterInfoResponse Info() => new()
    {
        NodeId = _self.NodeId,
        Role = _self.Role,
        Capacity = _self.Capacity,
        OcrMode = _self.OcrMode,
        Model = _self.Model,
        Dpi = _self.Dpi,
        EngineCount = _self.EngineCount,
        Healthy = true,
        ActiveSessions = _sessions.Count,
        PagesDone = PagesDone,
        LlmConfigured = _llm.IsUsable,
        NerConcurrency = AdvertisedNerConcurrency,
    };

    public ClusterHealthInfo BuildHealth() => new()
    {
        Enabled = true,
        Role = _self.Role,
        NodeId = _self.NodeId,
        AdvertiseUrl = _self.AdvertiseUrl,
        OcrMode = _self.OcrMode,
        Model = _self.Model,
        Dpi = _self.Dpi,
        Capacity = _self.Capacity,
        TokenSet = true,
        DistributedNer = _config.DistributedNer,
        Nodes =
        [
            new ClusterNodeHealth
            {
                NodeId = _self.NodeId,
                Url = string.IsNullOrWhiteSpace(_self.AdvertiseUrl) ? null : _self.AdvertiseUrl,
                Local = true,
                Healthy = true,
                Capacity = _self.Capacity,
                InFlight = Volatile.Read(ref _inFlight),
                PagesDone = PagesDone,
                OcrMode = _self.OcrMode,
                Model = _self.Model,
                Dpi = _self.Dpi,
                LlmConfigured = _llm.IsUsable,
                NerConcurrency = AdvertisedNerConcurrency,
            },
        ],
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled || !_config.IsWorker)
            return Task.CompletedTask;
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (CancellationTokenSource session in _sessions.Values)
            await session.CancelAsync().ConfigureAwait(false);
        if (_loop is null)
            return;
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Coordinator poked this node about a job. Idempotent if the session already exists.</summary>
    public bool TryStartNotified(ClusterNotifyRequest req, out string? error)
    {
        error = null;
        if (!_config.Enabled || !_config.IsWorker)
        {
            error = "This process is not a cluster worker.";
            return false;
        }

        if (req.Prefetch)
        {
            if (string.IsNullOrWhiteSpace(req.SourceUrl))
            {
                error = "sourceUrl is required for prefetch.";
                return false;
            }

            StartPrefetch(req.SourceUrl);
            return true;
        }

        if (string.IsNullOrWhiteSpace(req.JobId) || string.IsNullOrWhiteSpace(req.CoordinatorUrl))
        {
            error = "jobId and coordinatorUrl are required.";
            return false;
        }

        if (req.PageCount <= 0 || req.Dpi <= 0)
        {
            error = "dpi and pageCount are required.";
            return false;
        }

        if (_sessions.Count >= ClusterDispatchRules.MaxWorkerSessions && !_sessions.ContainsKey(req.JobId))
        {
            error = "Worker is at session capacity.";
            return false;
        }

        StartSession(
            req.JobId.Trim(),
            req.CoordinatorUrl.Trim().TrimEnd('/'),
            req.Dpi,
            req.PageCount,
            req.SourceUrl);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.CoordinatorUrl) &&
            !SameUrl(_config.CoordinatorUrl, _config.AdvertiseUrl))
        {
            await RegisterUntilSuccessAsync(ct).ConfigureAwait(false);
        }

        using PeriodicTimer heartbeat = new(TimeSpan.FromMilliseconds(Math.Max(1000, _config.HealthIntervalMs)));
        Task heartbeatTask = HeartbeatLoopAsync(heartbeat, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (string.IsNullOrWhiteSpace(_config.CoordinatorUrl) ||
                    SameUrl(_config.CoordinatorUrl, _config.AdvertiseUrl))
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    ClusterDispatchResponse? dispatch = await DispatchAsync(ct).ConfigureAwait(false);
                    if (dispatch is { Wait: false, JobId.Length: > 0, PdfPath.Length: > 0 })
                    {
                        StartSession(
                            dispatch.JobId,
                            _config.CoordinatorUrl,
                            dispatch.Dpi,
                            dispatch.PageCount,
                            dispatch.SourceUrl);
                    }
                    else
                        ClusterJobLog.DispatchWait(_logger, _config.VerboseDispatch, _self.NodeId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cluster dispatch poll failed");
                }

                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await Quiet(heartbeatTask).ConfigureAwait(false);
        }
    }

    private void StartSession(string jobId, string coordinatorUrl, int dpi, int pageCount, string? sourceUrl)
    {
        if (_sessions.Count >= ClusterDispatchRules.MaxWorkerSessions && !_sessions.ContainsKey(jobId))
        {
            _logger.LogDebug(
                "Cluster worker {NodeId} ignored job {JobId}: at session capacity",
                _self.NodeId,
                jobId);
            return;
        }

        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (!_sessions.TryAdd(jobId, sessionCts))
        {
            sessionCts.Dispose();
            return;
        }

        _recentlyLeft.TryRemove(jobId, out _);
        _ = Task.Run(
            () => SessionAsync(jobId, coordinatorUrl, dpi, pageCount, sourceUrl, sessionCts),
            CancellationToken.None);
    }

    private async Task SessionAsync(
        string jobId,
        string coordinatorUrl,
        int dpi,
        int pageCount,
        string? sourceUrl,
        CancellationTokenSource sessionCts)
    {
        CancellationToken ct = sessionCts.Token;
        int localDone = 0;
        int nerGroups = 0;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        DateTimeOffset progressAt = started;
        Task? nerTask = null;
        byte[]? renderPdf = null;
        try
        {
            _logger.LogInformation(
                "Cluster worker {NodeId} joining job {JobId} via {Coordinator} dpi={Dpi} pages={Pages} pdf={Source}",
                _self.NodeId,
                jobId,
                coordinatorUrl,
                dpi,
                pageCount,
                DescribePdfSource(sourceUrl));
            bool cached = TryGetCachedPdf(jobId, out byte[]? pdf);
            Task<byte[]>? shared = null;
            if (!cached && !string.IsNullOrWhiteSpace(sourceUrl))
            {
                Task<byte[]> fetch = GetOrStartUrlFetch(sourceUrl.Trim());
                if (fetch.IsCompletedSuccessfully)
                {
                    pdf = await fetch.ConfigureAwait(false);
                    RememberPdf(jobId, pdf);
                    cached = true;
                }
                else if (!fetch.IsCompleted)
                    shared = fetch;
            }

            if (!await JoinWithRetryAsync(coordinatorUrl, jobId, downloading: !cached, ct).ConfigureAwait(false))
                return;
            nerTask = RunsDistributedNer
                ? RunNerConsumersAsync(coordinatorUrl, jobId, () => Interlocked.Increment(ref nerGroups), ct)
                : null;
            if (!cached)
            {
                Stopwatch download = Stopwatch.StartNew();
                if (shared is not null)
                {
                    try
                    {
                        pdf = await shared.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            ex,
                            "Cluster worker {NodeId} job {JobId} source prefetch failed; falling back to the coordinator",
                            _self.NodeId,
                            jobId);
                        pdf = await DownloadPdfWithRetryAsync(coordinatorUrl, jobId, sourceUrl: null, ct)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    pdf = await DownloadPdfWithRetryAsync(coordinatorUrl, jobId, sourceUrl, ct).ConfigureAwait(false);
                }

                download.Stop();
                RememberPdf(jobId, pdf);
                _logger.LogInformation(
                    "Cluster worker {NodeId} job {JobId} PDF downloaded bytes={Bytes} ms={Ms:F0} via={Source}",
                    _self.NodeId,
                    jobId,
                    pdf.Length,
                    download.Elapsed.TotalMilliseconds,
                    DescribePdfSource(sourceUrl));
                if (!await JoinWithRetryAsync(coordinatorUrl, jobId, downloading: false, ct).ConfigureAwait(false))
                    return;
            }
            else
            {
                _logger.LogInformation(
                    "Cluster worker {NodeId} job {JobId} PDF cache hit bytes={Bytes}",
                    _self.NodeId,
                    jobId,
                    pdf!.Length);
            }

            byte[] pdfBytes = pdf ?? throw new InvalidOperationException("PDF was not downloaded.");
            renderPdf = pdfBytes;
            if (_config.PipelineOcr)
            {
                localDone += await RunPipelinedOcrAsync(
                    coordinatorUrl, jobId, dpi, pageCount, pdfBytes, started, progressAt, ct).ConfigureAwait(false);
            }
            else
            while (!ct.IsCancellationRequested)
            {
                ClusterClaimResponse? claim;
                try
                {
                    claim = await ClaimWithRetryAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cluster worker {NodeId} claim on {JobId} failed; retrying", _self.NodeId, jobId);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }
                if (claim is null)
                    break;
                if (claim.Done)
                    break;
                if (claim.Wait || claim.Pages is null || claim.Pages.Count == 0 || string.IsNullOrWhiteSpace(claim.BatchId))
                {
                    ClusterJobLog.ClaimWait(_logger, _config.VerboseDispatch, _self.NodeId, jobId);
                    await Task.Delay(Math.Clamp(claim.RetryAfterMs, 50, 2000), ct).ConfigureAwait(false);
                    continue;
                }

                int[] zeroBased = new int[claim.Pages.Count];
                for (int i = 0; i < claim.Pages.Count; i++)
                    zeroBased[i] = claim.Pages[i] - 1;

                Interlocked.Add(ref _inFlight, claim.Pages.Count);
                try
                {
                    List<OcrPageResult> results = [];
                    await _pipeline.RecognizeIndicesAsync(
                        pdfBytes,
                        pdfBytes.Length,
                        zeroBased,
                        dpi,
                        results.Add,
                        ct).ConfigureAwait(false);
                    await PostResultsAsync(coordinatorUrl, jobId, claim.BatchId, results, ct).ConfigureAwait(false);
                    Interlocked.Add(ref _pagesDone, results.Count);
                    localDone += results.Count;
                    ClusterJobLog.BatchDone(
                        _logger,
                        _config.VerboseDispatch,
                        _self.NodeId,
                        jobId,
                        claim.BatchId,
                        results.Count);
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (now - progressAt >= ClusterJobLog.ProgressInterval)
                    {
                        double rate = localDone / Math.Max(0.001, (now - started).TotalSeconds);
                        if (nerTask is null)
                        {
                            _logger.LogInformation(
                                "Cluster worker {NodeId} job {JobId} progress: localPages={Pages} jobPages={Total} {Rate:F1} pages/s",
                                _self.NodeId,
                                jobId,
                                localDone,
                                pageCount,
                                rate);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Cluster worker {NodeId} job {JobId} progress: localPages={Pages} jobPages={Total} {Rate:F1} pages/s nerGroups={NerGroups}",
                                _self.NodeId,
                                jobId,
                                localDone,
                                pageCount,
                                rate,
                                Volatile.Read(ref nerGroups));
                        }

                        progressAt = now;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cluster worker {NodeId} batch {Batch} failed", _self.NodeId, claim.BatchId);
                    await PostFailAsync(coordinatorUrl, jobId, claim.BatchId, ex.Message, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Add(ref _inFlight, -claim.Pages.Count);
                }
            }

            if (nerTask is not null)
                await nerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cluster worker {NodeId} session {JobId} ended with error", _self.NodeId, jobId);
        }
        finally
        {
            if (renderPdf is not null)
                _pipeline.ReleaseMappedPdf(renderPdf);
            if (nerTask is not null && !nerTask.IsCompleted)
            {
                try
                {
                    await sessionCts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (nerTask is not null)
                await Quiet(nerTask).ConfigureAwait(false);
            _sessions.TryRemove(jobId, out _);
            _recentlyLeft[jobId] = DateTimeOffset.UtcNow.UtcTicks;
            sessionCts.Dispose();
            _logger.LogInformation(
                "Cluster worker {NodeId} left job {JobId} localPages={Pages}",
                _self.NodeId,
                jobId,
                localDone);
        }
    }

    /// <summary>
    /// Claims the next pages while OCR is still running and posts each finished page
    /// without holding an engine across the HTTP call. <c>PipelineOcr</c> false keeps
    /// the claim-whole-batch loop.
    /// </summary>
    private async Task<int> RunPipelinedOcrAsync(
        string coordinatorUrl,
        string jobId,
        int dpi,
        int pageCount,
        byte[] pdfBytes,
        DateTimeOffset started,
        DateTimeOffset progressAt,
        CancellationToken ct)
    {
        int depth = Math.Max(4, _self.Capacity + Math.Max(0, _config.RenderAheadPages));
        Channel<(string BatchId, int Page)> feed = Channel.CreateBounded<(string, int)>(
            new BoundedChannelOptions(depth)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        Channel<(string BatchId, OcrPageResult Page)> posts = Channel.CreateBounded<(string, OcrPageResult)>(
            new BoundedChannelOptions(Math.Max(depth, 8))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        ConcurrentDictionary<string, int> remaining = new(StringComparer.Ordinal);
        ConcurrentDictionary<string, int> batchSize = new(StringComparer.Ordinal);
        ConcurrentDictionary<string, byte> poisoned = new(StringComparer.Ordinal);
        int localDone = 0;
        SemaphoreSlim wake = new(0, int.MaxValue);
        using CancellationTokenSource pipeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task ClaimLoopAsync()
        {
            try
            {
                while (!pipeCts.IsCancellationRequested)
                {
                    ClusterClaimResponse? claim;
                    try
                    {
                        claim = await ClaimWithRetryAsync(coordinatorUrl, jobId, pipeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (pipeCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Cluster worker {NodeId} claim on {JobId} failed; retrying", _self.NodeId, jobId);
                        await Task.Delay(500, pipeCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (claim is null || claim.Done)
                        break;
                    if (claim.Wait || claim.Pages is null || claim.Pages.Count == 0 || string.IsNullOrWhiteSpace(claim.BatchId))
                    {
                        ClusterJobLog.ClaimWait(_logger, _config.VerboseDispatch, _self.NodeId, jobId);
                        int retry = Math.Clamp(claim?.RetryAfterMs ?? 200, 50, 2000);
                        await wake.WaitAsync(TimeSpan.FromMilliseconds(retry), pipeCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    remaining[claim.BatchId] = claim.Pages.Count;
                    batchSize[claim.BatchId] = claim.Pages.Count;
                    Interlocked.Add(ref _inFlight, claim.Pages.Count);
                    foreach (int page in claim.Pages)
                        await feed.Writer.WriteAsync((claim.BatchId, page), pipeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (pipeCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
            }
            finally
            {
                feed.Writer.TryComplete();
            }
        }

        async Task OcrLoopAsync()
        {
            try
            {
                await _pipeline.RecognizeFeedAsync(
                    pdfBytes,
                    pdfBytes.Length,
                    feed.Reader,
                    dpi,
                    (page, batchId, token) => new ValueTask(posts.Writer.WriteAsync((batchId, page), token).AsTask()),
                    pipeCts.Token).ConfigureAwait(false);
            }
            finally
            {
                posts.Writer.TryComplete();
            }
        }

        async Task PostLoopAsync()
        {
            await foreach (var (batchId, page) in posts.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (poisoned.ContainsKey(batchId))
                    continue;

                try
                {
                    await PostResultsAsync(coordinatorUrl, jobId, batchId, [page], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (poisoned.TryAdd(batchId, 1) && remaining.TryGetValue(batchId, out int left) && left > 0)
                    {
                        remaining[batchId] = 0;
                        Interlocked.Add(ref _inFlight, -left);
                    }

                    _logger.LogWarning(ex, "Cluster worker {NodeId} batch {Batch} failed", _self.NodeId, batchId);
                    await PostFailAsync(coordinatorUrl, jobId, batchId, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                int still = remaining.AddOrUpdate(batchId, 0, static (_, n) => n - 1);
                Interlocked.Decrement(ref _inFlight);
                int doneNow = Interlocked.Increment(ref localDone);
                Interlocked.Increment(ref _pagesDone);
                if (still == 0 && batchSize.TryGetValue(batchId, out int size))
                {
                    ClusterJobLog.BatchDone(
                        _logger, _config.VerboseDispatch, _self.NodeId, jobId, batchId, size);
                }

                wake.Release();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - progressAt >= ClusterJobLog.ProgressInterval)
                {
                    progressAt = now;
                    double rate = doneNow / Math.Max(0.001, (now - started).TotalSeconds);
                    _logger.LogInformation(
                        "Cluster worker {NodeId} job {JobId} progress: localPages={Pages} jobPages={Total} {Rate:F1} pages/s",
                        _self.NodeId,
                        jobId,
                        doneNow,
                        pageCount,
                        rate);
                }
            }
        }

        Task claiming = ClaimLoopAsync();
        Task ocr = OcrLoopAsync();
        Task posting = PostLoopAsync();
        await Task.WhenAny(claiming, ocr, posting).ConfigureAwait(false);
        if ((ocr.IsFaulted || posting.IsFaulted) && !claiming.IsCompleted)
            await pipeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await claiming.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        Exception? failure = null;
        try
        {
            await ocr.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failure = ex;
            await pipeCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await posting.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        foreach (KeyValuePair<string, int> item in remaining)
        {
            if (item.Value <= 0 || !poisoned.TryAdd(item.Key, 1))
                continue;
            Interlocked.Add(ref _inFlight, -item.Value);
            await PostFailAsync(coordinatorUrl, jobId, item.Key, "incomplete batch", CancellationToken.None)
                .ConfigureAwait(false);
        }

        wake.Release();
        if (failure is not null)
            throw failure;
        ct.ThrowIfCancellationRequested();
        return Volatile.Read(ref localDone);
    }

    private async Task RegisterUntilSuccessAsync(CancellationToken ct)
    {
        var body = RegisterBody();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Post, _config.CoordinatorUrl + "/cluster/register");
                AddAuth(req);
                req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterRegisterRequest);
                using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    ClusterRegisterResponse? parsed = await JsonSerializer.DeserializeAsync(
                        stream, AppJsonContext.Default.ClusterRegisterResponse, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(parsed?.Warning))
                    {
                        _logger.LogWarning(
                            "Coordinator reports a model/dpi mismatch for {NodeId}: {Warning}",
                            _self.NodeId,
                            parsed.Warning);
                    }
                    else
                    {
                        _logger.LogInformation("Registered with coordinator {Url} as {NodeId}", _config.CoordinatorUrl, _self.NodeId);
                    }

                    return;
                }

                _logger.LogWarning("Cluster register returned {Status}; retrying", (int)resp.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cluster register to {Url} failed; retrying", _config.CoordinatorUrl);
            }

            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HeartbeatLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.CoordinatorUrl) || SameUrl(_config.CoordinatorUrl, _config.AdvertiseUrl))
            return;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var body = new ClusterHeartbeatRequest
                {
                    NodeId = _self.NodeId,
                    Capacity = _self.Capacity,
                    InFlight = Volatile.Read(ref _inFlight),
                    Healthy = true,
                    LlmConfigured = _llm.IsUsable,
                    NerConcurrency = AdvertisedNerConcurrency,
                };
                try
                {
                    using HttpRequestMessage req = new(HttpMethod.Post, _config.CoordinatorUrl + "/cluster/heartbeat");
                    AddAuth(req);
                    req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterHeartbeatRequest);
                    using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        ClusterJobLog.Heartbeat(
                            _logger,
                            _config.VerboseDispatch,
                            _self.NodeId,
                            body.InFlight,
                            body.Capacity);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Cluster heartbeat returned {Status}",
                            (int)resp.StatusCode);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cluster heartbeat failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<ClusterDispatchResponse?> DispatchAsync(CancellationToken ct)
    {
        var body = new ClusterDispatchRequest
        {
            NodeId = _self.NodeId,
            Capacity = _self.Capacity,
            ActiveSessions = _sessions.Count,
            ActiveJobs = ActiveJobIds(),
        };
        using HttpRequestMessage req = new(HttpMethod.Post, _config.CoordinatorUrl + "/cluster/dispatch");
        AddAuth(req);
        req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterDispatchRequest);
        using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.ClusterDispatchResponse, ct)
            .ConfigureAwait(false);
    }

    private void StartPrefetch(string sourceUrl)
    {
        string url = sourceUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning(
                "Cluster worker {NodeId} ignored a prefetch whose sourceUrl is not absolute http(s)",
                _self.NodeId);
            return;
        }

        _logger.LogInformation(
            "Cluster worker {NodeId} prefetching source PDF {Source}",
            _self.NodeId,
            DescribePdfSource(url));
        _ = GetOrStartUrlFetch(url);
    }

    /// <summary>
    /// One download per source URL, shared by the prefetch notify and the later job session.
    /// </summary>
    private Task<byte[]> GetOrStartUrlFetch(string url)
    {
        if (_urlFetches.TryGetValue(url, out Task<byte[]>? existing))
            return existing;

        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_urlFetches.TryAdd(url, gate.Task))
        {
            return _urlFetches.TryGetValue(url, out Task<byte[]>? winner)
                ? winner
                : GetOrStartUrlFetch(url);
        }

        lock (_urlFetchOrderGate)
            _urlFetchOrder.Add(url);
        TrimUrlFetches(url);
        _ = FinishUrlFetchAsync(url, gate);
        return gate.Task;
    }

    private async Task FinishUrlFetchAsync(string url, TaskCompletionSource<byte[]> gate)
    {
        try
        {
            byte[] bytes = await DownloadUrlForShareAsync(url).ConfigureAwait(false);
            gate.TrySetResult(bytes);
        }
        catch (Exception ex)
        {
            _urlFetches.TryRemove(new KeyValuePair<string, Task<byte[]>>(url, gate.Task));
            lock (_urlFetchOrderGate)
                _urlFetchOrder.Remove(url);
            gate.TrySetException(ex);
        }
    }

    private async Task<byte[]> DownloadUrlForShareAsync(string url)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await DownloadExactAsync(url, prepare: null, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                _logger.LogWarning(
                    ex,
                    "Cluster worker {NodeId} source PDF prefetch failed (attempt {Attempt}); retrying",
                    _self.NodeId,
                    attempt + 1);
                await Task.Delay(Backoff(attempt), _cts.Token).ConfigureAwait(false);
            }
        }

        throw last ?? new HttpRequestException("PDF prefetch failed");
    }

    private void TrimUrlFetches(string keep)
    {
        lock (_urlFetchOrderGate)
        {
            int i = 0;
            while (_urlFetches.Count > MaxCachedPdfs && i < _urlFetchOrder.Count)
            {
                string key = _urlFetchOrder[i];
                if (string.Equals(key, keep, StringComparison.Ordinal) ||
                    !_urlFetches.TryGetValue(key, out Task<byte[]>? task) ||
                    !task.IsCompletedSuccessfully)
                {
                    i++;
                    continue;
                }

                _urlFetchOrder.RemoveAt(i);
                _urlFetches.TryRemove(new KeyValuePair<string, Task<byte[]>>(key, task));
            }
        }
    }

    private async Task<byte[]> DownloadPdfWithRetryAsync(
        string coordinatorUrl,
        string jobId,
        string? sourceUrl,
        CancellationToken ct)
    {
        Exception? last = null;
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    return await DownloadExactAsync(sourceUrl, prepare: null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    last = ex;
                    _logger.LogWarning(
                        ex,
                        "Cluster worker {NodeId} source PDF download failed (attempt {Attempt}); retrying",
                        _self.NodeId,
                        attempt + 1);
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                }
            }

            _logger.LogWarning(
                last,
                "Cluster worker {NodeId} job {JobId} source PDF unavailable; falling back to the coordinator",
                _self.NodeId,
                jobId);
        }

        string coordinatorPdf = coordinatorUrl + "/cluster/jobs/" + jobId + "/pdf";
        for (int attempt = 0; attempt < 4 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                return await DownloadExactAsync(coordinatorPdf, AddAuth, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                _logger.LogWarning(
                    ex,
                    "Cluster worker {NodeId} coordinator PDF download failed (attempt {Attempt}); retrying",
                    _self.NodeId,
                    attempt + 1);
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new HttpRequestException("PDF download failed");
    }

    private async Task<byte[]> DownloadExactAsync(
        string url,
        Action<HttpRequestMessage>? prepare,
        CancellationToken ct)
    {
        ParallelPdfDownloader.DownloadResult download =
            await _downloader.DownloadAsync(url, ct, prepare).ConfigureAwait(false);
        using RentedBuffer buffer = download.Buffer;
        if (buffer.Length <= 0 || buffer.Length > ClusterCoordinator.MaxPdfBytes)
            throw new InvalidOperationException("PDF exceeds 300 MB.");
        byte[] exact = new byte[buffer.Length];
        buffer.Span.CopyTo(exact);
        _logger.LogDebug(
            "PDF fetch mode={Mode} bytes={Bytes} ms={Ms:F0}",
            download.Mode,
            exact.Length,
            download.ElapsedMs);
        return exact;
    }

    private async Task<bool> JoinWithRetryAsync(
        string coordinatorUrl,
        string jobId,
        bool downloading,
        CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await JoinAsync(coordinatorUrl, jobId, downloading, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogInformation(
                    "Cluster worker {NodeId} job {JobId} join closed ({Status})",
                    _self.NodeId,
                    jobId,
                    (int?)ex.StatusCode);
                return false;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                int delay = Backoff(Math.Min(attempt, 6));
                _logger.LogWarning(
                    ex,
                    "Cluster worker {NodeId} join {JobId} failed; retrying in {Delay}ms",
                    _self.NodeId,
                    jobId,
                    delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        if (last is not null)
            _logger.LogWarning(last, "Cluster worker {NodeId} stopped joining {JobId}", _self.NodeId, jobId);
        return false;
    }

    private async Task JoinAsync(string coordinatorUrl, string jobId, bool downloading, CancellationToken ct)
    {
        var body = new ClusterJoinRequest
        {
            NodeId = _self.NodeId,
            Capacity = _self.Capacity,
            Downloading = downloading,
        };
        using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/join");
        AddAuth(req);
        req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterJoinRequest);
        using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<ClusterClaimResponse?> ClaimWithRetryAsync(
        string coordinatorUrl,
        string jobId,
        CancellationToken ct)
    {
        for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                return await ClaimAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Conflict)
            {
                return null;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                int delay = Backoff(Math.Min(attempt, 6));
                _logger.LogWarning(
                    ex,
                    "Cluster worker {NodeId} claim {JobId} failed; retrying in {Delay}ms",
                    _self.NodeId,
                    jobId,
                    delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new OperationCanceledException(ct);
    }

    private async Task<ClusterClaimResponse?> ClaimAsync(string coordinatorUrl, string jobId, CancellationToken ct)
    {
        var body = new ClusterClaimRequest { NodeId = _self.NodeId, MaxPages = _self.Capacity };
        using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/claim");
        AddAuth(req);
        req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterClaimRequest);
        using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.ClusterClaimResponse, ct)
            .ConfigureAwait(false);
    }

    private async Task PostResultsAsync(
        string coordinatorUrl,
        string jobId,
        string batchId,
        List<OcrPageResult> pages,
        CancellationToken ct)
    {
        var body = new ClusterResultRequest
        {
            NodeId = _self.NodeId,
            BatchId = batchId,
            Pages = pages,
        };
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/result");
                AddAuth(req);
                req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterResultRequest);
                using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new HttpRequestException("result post failed");
    }

    private async Task PostFailAsync(
        string coordinatorUrl,
        string jobId,
        string batchId,
        string error,
        CancellationToken ct)
    {
        try
        {
            var body = new ClusterFailRequest
            {
                NodeId = _self.NodeId,
                BatchId = batchId,
                Error = error.Length > 300 ? error[..300] : error,
            };
            using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/fail");
            AddAuth(req);
            req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterFailRequest);
            using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cluster fail callback could not be delivered");
        }
    }

    private async Task RunNerConsumersAsync(
        string coordinatorUrl,
        string jobId,
        Action onGroupDone,
        CancellationToken ct)
    {
        int consumers = AdvertisedNerConcurrency;
        if (consumers <= 0)
            return;
        _logger.LogInformation(
            "Cluster worker {NodeId} job {JobId} distributed NER: concurrency={Concurrency}",
            _self.NodeId,
            jobId,
            consumers);
        Task[] tasks = new Task[consumers];
        for (int i = 0; i < consumers; i++)
            tasks[i] = NerConsumerAsync(coordinatorUrl, jobId, onGroupDone, ct);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task NerConsumerAsync(string coordinatorUrl, string jobId, Action onGroupDone, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ClusterNerClaimResponse? claim;
            try
            {
                claim = await ClaimNerWithRetryAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cluster worker {NodeId} NER claim on {JobId} failed; retrying", _self.NodeId, jobId);
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }
            if (claim is null || claim.Done)
                return;
            if (claim.Wait)
            {
                ClusterJobLog.ClaimWait(_logger, _config.VerboseDispatch, _self.NodeId, jobId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(claim.GroupId) || string.IsNullOrWhiteSpace(claim.PromptText))
            {
                ClusterJobLog.ClaimWait(_logger, _config.VerboseDispatch, _self.NodeId, jobId);
                await Task.Delay(Math.Clamp(claim.RetryAfterMs <= 0 ? 300 : claim.RetryAfterMs, 50, 2000), ct).ConfigureAwait(false);
                continue;
            }

            var bodies = new OcrPageResult[claim.PageTexts?.Count ?? 0];
            if (claim.PageTexts is not null)
            {
                for (int i = 0; i < claim.PageTexts.Count; i++)
                {
                    bodies[i] = new OcrPageResult
                    {
                        Page = claim.PageTexts[i].Page,
                        Text = claim.PageTexts[i].Text ?? "",
                    };
                }
            }

            var assignment = new ClusterNerAssignment
            {
                Kind = ClusterNerClaimKind.Group,
                GroupId = claim.GroupId,
                Pages = claim.Pages?.ToArray() ?? [],
                Bodies = bodies,
                PromptText = claim.PromptText,
                Lookahead = claim.Lookahead,
                LookaheadPage = claim.LookaheadPage,
                LeaseMs = claim.LeaseMs,
            };

            try
            {
                ClusterNerResultRequest result = await _llm.ExtractDistributedAsync(assignment, ct).ConfigureAwait(false);
                result.NodeId = _self.NodeId;
                result.GroupId = claim.GroupId;
                int accepted = await PostNerAsync(coordinatorUrl, jobId, result, ct).ConfigureAwait(false);
                if (accepted > 0)
                {
                    onGroupDone();
                    ClusterJobLog.NerDone(
                        _logger,
                        _config.VerboseDispatch,
                        _self.NodeId,
                        jobId,
                        claim.GroupId,
                        result.Entities?.Count ?? 0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cluster worker {NodeId} NER group {Group} failed", _self.NodeId, claim.GroupId);
                await PostNerFailAsync(coordinatorUrl, jobId, claim.GroupId, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<ClusterNerClaimResponse?> ClaimNerWithRetryAsync(
        string coordinatorUrl,
        string jobId,
        CancellationToken ct)
    {
        for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                return await ClaimNerAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Conflict)
            {
                return null;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                int delay = Backoff(Math.Min(attempt, 6));
                _logger.LogWarning(
                    ex,
                    "Cluster worker {NodeId} NER claim {JobId} failed; retrying in {Delay}ms",
                    _self.NodeId,
                    jobId,
                    delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new OperationCanceledException(ct);
    }

    private async Task<ClusterNerClaimResponse?> ClaimNerAsync(string coordinatorUrl, string jobId, CancellationToken ct)
    {
        var body = new ClusterNerClaimRequest
        {
            NodeId = _self.NodeId,
            LlmConfigured = true,
            NerConcurrency = AdvertisedNerConcurrency,
            WaitMs = ClusterCoordinator.NerClaimWaitMs,
        };
        using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/ner/claim");
        AddAuth(req);
        req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterNerClaimRequest);
        using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.ClusterNerClaimResponse, ct)
            .ConfigureAwait(false);
    }

    private async Task<int> PostNerAsync(string coordinatorUrl, string jobId, ClusterNerResultRequest body, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/ner/result");
                AddAuth(req);
                req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterNerResultRequest);
                using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                ClusterAck? ack = await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.ClusterAck, ct)
                    .ConfigureAwait(false);
                return ack?.Accepted ?? 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new HttpRequestException("NER result post failed");
    }

    private async Task PostNerFailAsync(string coordinatorUrl, string jobId, string groupId, string error, CancellationToken ct)
    {
        try
        {
            var body = new ClusterFailRequest
            {
                NodeId = _self.NodeId,
                BatchId = groupId,
                Error = error.Length > 300 ? error[..300] : error,
            };
            using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/ner/fail");
            AddAuth(req);
            req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterFailRequest);
            using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cluster NER fail callback could not be delivered");
        }
    }

    private ClusterRegisterRequest RegisterBody() => new()
    {
        NodeId = _self.NodeId,
        BaseUrl = _self.AdvertiseUrl,
        Capacity = _self.Capacity,
        OcrMode = _self.OcrMode,
        Model = _self.Model,
        Dpi = _self.Dpi,
        EngineCount = _self.EngineCount,
        LlmConfigured = _llm.IsUsable,
        NerConcurrency = AdvertisedNerConcurrency,
    };

    private HttpClient Client() => _httpFactory.CreateClient(ClusterCoordinator.HttpClientName);

    private void AddAuth(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);

    private List<string> ActiveJobIds()
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(RecentLeaveCooldown).UtcTicks;
        var ids = new List<string>(_sessions.Keys);
        foreach ((string jobId, long left) in _recentlyLeft)
        {
            if (left < cutoff)
            {
                _recentlyLeft.TryRemove(jobId, out _);
                continue;
            }

            if (!ids.Contains(jobId))
                ids.Add(jobId);
        }

        return ids;
    }

    private bool TryGetCachedPdf(string jobId, out byte[]? pdf) =>
        _pdfCache.TryGetValue(jobId, out pdf);

    private void RememberPdf(string jobId, byte[] pdf)
    {
        _pdfCache[jobId] = pdf;
        lock (_pdfCacheOrder)
        {
            _pdfCacheKeys.Remove(jobId);
            _pdfCacheKeys.Add(jobId);
            while (_pdfCacheKeys.Count > MaxCachedPdfs)
            {
                string oldest = _pdfCacheKeys[0];
                _pdfCacheKeys.RemoveAt(0);
                if (!string.Equals(oldest, jobId, StringComparison.Ordinal))
                    _pdfCache.TryRemove(oldest, out _);
            }
        }
    }

    private static string DescribePdfSource(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return "coordinator";
        return Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri)
            ? "source:" + uri.Host
            : "source";
    }

    private static int Backoff(int attempt)
    {
        int shift = Math.Clamp(attempt, 0, 4);
        return Math.Min(2_000, 200 << shift);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException or IOException or TaskCanceledException or TimeoutException)
            return true;
        if (ex is OperationCanceledException)
            return true;
        return ex.InnerException is not null && IsTransient(ex.InnerException);
    }

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
