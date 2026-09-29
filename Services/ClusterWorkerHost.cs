using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    private readonly ILogger<ClusterWorkerHost> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new(StringComparer.Ordinal);
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
        ILogger<ClusterWorkerHost> logger)
    {
        _config = config;
        _self = self;
        _pipeline = pipeline;
        _llm = llm;
        _httpFactory = httpFactory;
        _logger = logger;
    }

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

        if (_sessions.Count >= 2 && !_sessions.ContainsKey(req.JobId))
        {
            error = "Worker is at session capacity.";
            return false;
        }

        StartSession(req.JobId.Trim(), req.CoordinatorUrl.Trim().TrimEnd('/'), req.Dpi, req.PageCount);
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
                        StartSession(dispatch.JobId, _config.CoordinatorUrl, dispatch.Dpi, dispatch.PageCount);
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

    private void StartSession(string jobId, string coordinatorUrl, int dpi, int pageCount)
    {
        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (!_sessions.TryAdd(jobId, sessionCts))
        {
            sessionCts.Dispose();
            return;
        }

        _ = Task.Run(() => SessionAsync(jobId, coordinatorUrl, dpi, pageCount, sessionCts), CancellationToken.None);
    }

    private async Task SessionAsync(string jobId, string coordinatorUrl, int dpi, int pageCount, CancellationTokenSource sessionCts)
    {
        CancellationToken ct = sessionCts.Token;
        int localDone = 0;
        int nerGroups = 0;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        DateTimeOffset progressAt = started;
        Task? nerTask = null;
        try
        {
            _logger.LogInformation(
                "Cluster worker {NodeId} joining job {JobId} via {Coordinator} dpi={Dpi} pages={Pages}",
                _self.NodeId,
                jobId,
                coordinatorUrl,
                dpi,
                pageCount);
            Stopwatch download = Stopwatch.StartNew();
            byte[] pdf = await DownloadPdfAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
            download.Stop();
            _logger.LogInformation(
                "Cluster worker {NodeId} job {JobId} PDF downloaded bytes={Bytes} ms={Ms:F0}",
                _self.NodeId,
                jobId,
                pdf.Length,
                download.Elapsed.TotalMilliseconds);
            await JoinAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
            nerTask = RunsDistributedNer
                ? RunNerConsumersAsync(coordinatorUrl, jobId, () => Interlocked.Increment(ref nerGroups), ct)
                : null;

            while (!ct.IsCancellationRequested)
            {
                ClusterClaimResponse? claim = await ClaimAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
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
                        pdf,
                        pdf.Length,
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
                catch (OperationCanceledException)
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
            sessionCts.Dispose();
            _logger.LogInformation(
                "Cluster worker {NodeId} left job {JobId} localPages={Pages}",
                _self.NodeId,
                jobId,
                localDone);
        }
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
            ActiveJobs = _sessions.Keys.ToList(),
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

    private async Task<byte[]> DownloadPdfAsync(string coordinatorUrl, string jobId, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, coordinatorUrl + "/cluster/jobs/" + jobId + "/pdf");
        AddAuth(req);
        using HttpResponseMessage resp = await Client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long? length = resp.Content.Headers.ContentLength;
        if (length is > ClusterCoordinator.MaxPdfBytes)
            throw new InvalidOperationException("PDF exceeds 300 MB.");
        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(length is > 0 and < int.MaxValue ? (int)length : 64 * 1024);
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
                break;
            if (ms.Length + n > ClusterCoordinator.MaxPdfBytes)
                throw new InvalidOperationException("PDF exceeds 300 MB.");
            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }

    private async Task JoinAsync(string coordinatorUrl, string jobId, CancellationToken ct)
    {
        var body = new ClusterJoinRequest { NodeId = _self.NodeId, Capacity = _self.Capacity };
        using HttpRequestMessage req = new(HttpMethod.Post, coordinatorUrl + "/cluster/jobs/" + jobId + "/join");
        AddAuth(req);
        req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterJoinRequest);
        using HttpResponseMessage resp = await Client().SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
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
            ClusterNerClaimResponse? claim = await ClaimNerAsync(coordinatorUrl, jobId, ct).ConfigureAwait(false);
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
            catch (OperationCanceledException)
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
