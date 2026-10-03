using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MiniOcr;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Coordinates pull-based OCR. Each worker downloads the PDF once, then claims page
/// batches sized by its capacity. The coordinator's own engine pool is the local node.
/// Images are not shipped: raster stays on the machine that OCRs the page.
/// </summary>
public sealed class ClusterCoordinator : IHostedService
{
    public const string HttpClientName = "MiniOcr.Cluster";
    public const int MaxPdfBytes = 300 * 1024 * 1024;

    private readonly ClusterRuntimeConfig _config;
    private readonly ClusterNodeRegistry _registry;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NacosClient? _nacos;
    private readonly LlmEntityExtractor _llm;
    private readonly ILogger<ClusterCoordinator> _logger;
    private readonly ConcurrentDictionary<string, ClusterJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _publish = new();
    private readonly CancellationTokenSource _cts = new();
    private ClusterLastJobHealth? _lastJob;
    private Task? _healthLoop;

    public ClusterCoordinator(
        ClusterRuntimeConfig config,
        ClusterNodeRegistry registry,
        IHttpClientFactory httpFactory,
        ILogger<ClusterCoordinator> logger,
        LlmEntityExtractor llm,
        NacosClient? nacos = null)
    {
        _config = config;
        _registry = registry;
        _httpFactory = httpFactory;
        _nacos = nacos;
        _llm = llm;
        _logger = logger;
    }

    public bool DistributedNer => _config.DistributedNer;

    public bool ShouldDistribute() =>
        _config.Enabled && _config.IsCoordinator && _registry.HasPotentialRemote;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled || !_config.IsCoordinator)
            return;
        _healthLoop = Task.Run(() => HealthLoopAsync(_cts.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_healthLoop is null)
            return;
        try
        {
            await _healthLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ClusterHealthInfo BuildHealth()
    {
        List<ClusterScheduleSnapshot> active = [];
        ClusterLastJobHealth? last;
        lock (_publish)
        {
            foreach (ClusterJob job in _jobs.Values)
                active.Add(job.Scheduler.Snapshot());
            last = _lastJob;
        }

        ClusterHealthInfo health = _registry.BuildHealth(active, last);
        health.DistributedNer = _config.DistributedNer;
        return health;
    }

    public void PublishLastJobHash(string jobId, string hash)
    {
        lock (_publish)
        {
            if (_lastJob is not null && _lastJob.JobId == jobId)
                _lastJob.PageTextSha256 = hash;
        }
    }

    public ClusterJob? FindJob(string jobId) =>
        _jobs.TryGetValue(jobId, out ClusterJob? job) ? job : null;

    public string? Register(ClusterRegisterRequest req)
    {
        string? warning = _registry.Register(req);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            _logger.LogWarning(
                "Cluster node {NodeId} at {Url} does not match this node ({Warning}). Page text may differ across machines.",
                req.NodeId,
                req.BaseUrl,
                warning);
        }
        else
        {
            _logger.LogInformation(
                "Cluster node registered: {NodeId} url={Url} capacity={Capacity} mode={Mode} model={Model} dpi={Dpi}",
                req.NodeId,
                req.BaseUrl,
                req.Capacity,
                req.OcrMode,
                req.Model,
                req.Dpi);
        }

        if (req.Capacity > 0)
        {
            foreach (ClusterJob job in _jobs.Values)
                job.Scheduler.SetCapacity(req.NodeId, req.Capacity);
        }

        NoteNerCapacity(req.NodeId, req.LlmConfigured, req.NerConcurrency);
        return warning;
    }

    public void Heartbeat(ClusterHeartbeatRequest req)
    {
        _registry.Heartbeat(req);
        if (req.Capacity > 0)
        {
            foreach (ClusterJob job in _jobs.Values)
                job.Scheduler.SetCapacity(req.NodeId, req.Capacity);
        }

        NoteNerCapacity(req.NodeId, req.LlmConfigured, req.NerConcurrency);
        ClusterJobLog.Heartbeat(_logger, _config.VerboseDispatch, req.NodeId, req.InFlight, req.Capacity);
    }

    public ClusterDispatchResponse Dispatch(
        string nodeId,
        int capacity,
        IReadOnlyList<string>? activeJobs,
        int? activeSessions = null)
    {
        if (capacity > 0 && !string.IsNullOrWhiteSpace(nodeId))
        {
            foreach (ClusterJob job in _jobs.Values)
                job.Scheduler.SetCapacity(nodeId, capacity);
        }

        var busy = new HashSet<string>(StringComparer.Ordinal);
        if (activeJobs is not null)
        {
            foreach (string id in activeJobs)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    busy.Add(id);
            }
        }

        int sessions = ClusterDispatchRules.SessionCount(activeSessions, busy.Count);
        if (!string.IsNullOrWhiteSpace(nodeId) && ClusterDispatchRules.AtSessionCap(sessions))
        {
            ClusterJobLog.DispatchWait(_logger, _config.VerboseDispatch, nodeId);
            return new ClusterDispatchResponse { Wait = true, RetryAfterMs = 300 };
        }

        bool nodeCanNer = !string.IsNullOrWhiteSpace(nodeId) && _registry.NerCapacity(nodeId) > 0;
        foreach (ClusterJob job in _jobs.Values.OrderBy(j => j.Started))
        {
            if (job.IsFinished)
                continue;
            if (string.IsNullOrWhiteSpace(nodeId))
                continue;
            if (busy.Contains(job.Id))
                continue;
            bool ocrComplete = job.Scheduler.IsComplete;
            bool nerPending = job.Ner is { IsComplete: false };
            if (!ClusterDispatchRules.ShouldOfferJob(ocrComplete, nerPending, nodeCanNer))
                continue;
            return new ClusterDispatchResponse
            {
                Wait = false,
                JobId = job.Id,
                Dpi = job.Dpi,
                PageCount = job.PageCount,
                PdfPath = "/cluster/jobs/" + job.Id + "/pdf",
                SourceUrl = string.IsNullOrWhiteSpace(job.SourceUrl) ? null : job.SourceUrl,
                RetryAfterMs = 200,
            };
        }

        ClusterJobLog.DispatchWait(_logger, _config.VerboseDispatch, nodeId);
        return new ClusterDispatchResponse { Wait = true, RetryAfterMs = 300 };
    }

    public bool Join(ClusterJob job, string nodeId, int capacity, bool downloading = false)
    {
        if (job.IsFinished || string.IsNullOrWhiteSpace(nodeId))
            return false;
        bool first = job.Joined.TryAdd(nodeId, 1);
        if (capacity > 0)
            job.Scheduler.SetCapacity(nodeId, capacity);
        if (downloading)
            job.MarkDownloading(nodeId);
        else
            job.ClearDownloading(nodeId);
        if (job.ExpectRemote)
            job.Scheduler.SetHoldLocalWindow(job.ShouldHoldLocal(DateTimeOffset.UtcNow));
        if (downloading)
        {
            _logger.LogInformation(
                "Cluster job {JobId} node {NodeId} joined, downloading PDF (capacity={Capacity})",
                job.Id,
                nodeId,
                capacity);
        }
        else if (first)
        {
            _logger.LogInformation(
                "Cluster job {JobId} node {NodeId} joined (capacity={Capacity})",
                job.Id,
                nodeId,
                capacity);
        }
        else
        {
            _logger.LogInformation(
                "Cluster job {JobId} node {NodeId} PDF ready (capacity={Capacity})",
                job.Id,
                nodeId,
                capacity);
        }

        return true;
    }

    public ClusterClaimResponse Claim(ClusterJob job, string nodeId, int maxPages)
    {
        if (job.IsFinished)
            return new ClusterClaimResponse { Done = true };

        if (maxPages <= 0)
            maxPages = 1;
        if (!string.IsNullOrWhiteSpace(nodeId))
            job.Scheduler.SetCapacity(nodeId, Math.Max(1, maxPages));
        if (job.Ner is not null)
            job.Scheduler.SetNerBlocker(job.Ner.EarliestMissingPage());

        ClusterClaim claim = job.Scheduler.Claim(nodeId, maxPages, DateTimeOffset.UtcNow);
        LogExpiries(job);
        LogClaim(job.Id, nodeId, claim);

        return new ClusterClaimResponse
        {
            Done = claim.Kind == ClusterClaimKind.Done,
            Wait = claim.Kind == ClusterClaimKind.Wait,
            BatchId = claim.Kind == ClusterClaimKind.Batch ? claim.BatchId : null,
            Pages = claim.Kind == ClusterClaimKind.Batch ? claim.Pages.ToList() : null,
            LeaseMs = claim.LeaseMs,
            RetryAfterMs = claim.RetryAfterMs,
            Speculative = claim.Speculative,
        };
    }

    public int AcceptResults(ClusterJob job, string batchId, IReadOnlyList<OcrPageResult>? pages)
    {
        if (pages is null || pages.Count == 0)
            return 0;
        int accepted = 0;
        job.DeferProgress = true;
        try
        {
            foreach (OcrPageResult page in pages)
            {
                if (job.TryAccept(batchId, page))
                    accepted++;
            }
        }
        finally
        {
            job.DeferProgress = false;
        }

        if (accepted > 0)
        {
            if (_config.VerboseDispatch)
            {
                _logger.LogInformation(
                    "Cluster job {JobId} batch {Batch} accepted pages={Pages}",
                    job.Id,
                    batchId,
                    accepted);
            }
            else
            {
                _logger.LogDebug(
                    "Cluster job {JobId} batch {Batch} accepted pages={Pages}",
                    job.Id,
                    batchId,
                    accepted);
            }

            MaybeLogProgress(job);
        }

        return accepted;
    }

    public void FailBatch(ClusterJob job, string nodeId, string batchId, string? error)
    {
        int[] released = job.Scheduler.ReleaseBatch(batchId);
        if (released.Length == 0)
            return;
        _logger.LogWarning(
            "Cluster job {JobId} requeued pages [{Pages}] from node {Node} batch {Batch}: {Error}",
            job.Id,
            string.Join(",", released),
            nodeId,
            batchId,
            string.IsNullOrWhiteSpace(error) ? "failed" : error);
    }

    public async Task<ClusterRunResult> RunJobAsync(
        byte[] pdf,
        int pdfLength,
        int pageCount,
        int dpi,
        string? sourceUrl,
        Func<int[], string, ClusterJob, CancellationToken, Task> recognizeLocal,
        Action<OcrPageResult> onAccepted,
        bool distributeNer,
        CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        List<ClusterRemote> remotes = _registry.Remotes();
        var scheduler = new ClusterPageScheduler(new ClusterScheduleOptions
        {
            PageCount = pageCount,
            LocalNodeId = _config.NodeId,
            PagesPerBatch = _config.PagesPerBatch,
            LeaseFloorMs = _config.LeaseFloorMs,
            PageTimeoutMs = _config.PageTimeoutMs,
            LeaseCapMs = Math.Max(_config.LeaseFloorMs, 180_000),
            SpeculativeTailPages = _config.SpeculativeTailPages,
            ExpectedNodes = Math.Max(1, 1 + remotes.Count),
        });

        int localCap = Math.Max(1, _registry.LocalCapacity);
        scheduler.SetCapacity(_config.NodeId, localCap);
        foreach (ClusterRemote remote in remotes)
            scheduler.SetCapacity(remote.NodeId, Math.Max(1, remote.Capacity));

        bool expectRemote = remotes.Count > 0;
        scheduler.SetHoldLocalWindow(expectRemote && _config.JoinGraceMs > 0);

        var job = new ClusterJob(id, scheduler, pdf, pdfLength, pageCount, dpi, sourceUrl)
        {
            ExpectRemote = expectRemote,
        };
        ClusterNerScheduler? ner = null;
        int localNerCap = 0;
        if (distributeNer && _config.DistributedNer)
        {
            int leaseMs = Math.Clamp((_llm.Config.TimeoutSeconds + 15) * 1000, 20_000, 600_000);
            localNerCap = _llm.IsUsable ? Math.Clamp(_llm.Config.MaxConcurrency, 1, 32) : 0;
            ner = new ClusterNerScheduler(new ClusterNerOptions
            {
                PageCount = pageCount,
                PagesPerRequest = _llm.Config.PagesPerRequest,
                MaxChars = _llm.Config.MaxCharsPerRequest,
                LeaseMs = leaseMs,
                LocalNodeId = _config.NodeId,
                // Reserve a few seconds for workers that have NER capacity but have not issued
                // their first long-poll yet, then keep assigning to the least-loaded active node.
                FairHoldMs = 3_000,
                ActiveMs = 5_000,
            });
            ner.SetNerCapacity(_config.NodeId, localNerCap);
            foreach (ClusterRemote remote in remotes)
                ner.SetNerCapacity(remote.NodeId, _registry.NerCapacity(remote.NodeId));
            _logger.LogInformation(
                "Cluster job {JobId} distributed NER on: pagesPerRequest={PagesPerRequest}, maxChars={MaxChars}, localNerConcurrency={LocalNer}",
                id,
                _llm.Config.PagesPerRequest,
                _llm.Config.MaxCharsPerRequest,
                localNerCap);
        }

        job.Ner = ner;
        job.SetAccepted(page =>
        {
            onAccepted(page);
            if (!job.DeferProgress)
                MaybeLogProgress(job);
        });
        _jobs[id] = job;

        WarnModelMismatch(remotes, dpi);
        _logger.LogInformation(
            "Cluster job {JobId} start: pages={Pages}, pdfBytes={Bytes}, dpi={Dpi}, remotes={Remotes}, localCapacity={LocalCap}, transport=pdf-once+page-ranges, source={Source}",
            id,
            pageCount,
            pdfLength,
            dpi,
            remotes.Count,
            localCap,
            string.IsNullOrWhiteSpace(sourceUrl) ? "coordinator-only" : "origin+coordinator");

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task notify = NotifyWorkersAsync(job, remotes, linked.Token);
        Task grace = HoldGraceAsync(job, expectRemote, linked.Token);
        Task deadline = DeadlineAsync(job, linked.Token);
        Task? localNer = ner is null ? null : RunLocalNerAsync(job, ner, localNerCap, linked.Token);

        OcrEntities? distributedEntities = null;
        bool usedDistributed = false;
        int localFailures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (scheduler.IsComplete)
                    break;

                if (ner is not null)
                    scheduler.SetNerBlocker(ner.EarliestMissingPage());
                ClusterClaim claim = scheduler.Claim(_config.NodeId, localCap, DateTimeOffset.UtcNow);
                LogExpiries(job);
                if (claim.Kind == ClusterClaimKind.Done)
                    break;
                if (claim.Kind == ClusterClaimKind.Wait)
                {
                    await scheduler.WaitForChangeAsync(claim.Version, TimeSpan.FromSeconds(1), ct)
                        .ConfigureAwait(false);
                    continue;
                }

                LogClaim(id, _config.NodeId, claim);

                try
                {
                    await recognizeLocal(claim.Pages, claim.BatchId, job, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    scheduler.ReleaseBatch(claim.BatchId);
                    throw;
                }
                catch (Exception ex)
                {
                    int[] released = scheduler.ReleaseBatch(claim.BatchId);
                    localFailures++;
                    _logger.LogWarning(
                        ex,
                        "Cluster job {JobId} local batch {Batch} failed ({Failures}); requeued [{Pages}]",
                        id,
                        claim.BatchId,
                        localFailures,
                        string.Join(",", released));
                    if (localFailures >= 3)
                        throw;
                    continue;
                }

                int[] leftover = scheduler.ReleaseBatch(claim.BatchId);
                if (leftover.Length > 0)
                {
                    _logger.LogWarning(
                        "Cluster job {JobId} local batch {Batch} incomplete; requeued [{Pages}]",
                        id,
                        claim.BatchId,
                        string.Join(",", leftover));
                }
            }

            ct.ThrowIfCancellationRequested();
            job.DrainAccepts();
            if (!scheduler.IsComplete)
            {
                throw new InvalidOperationException(
                    $"Cluster job {id} ended with {scheduler.Snapshot().Done}/{pageCount} pages.");
            }

            if (ner is not null)
            {
                ner.Seal();
                if (localNer is not null)
                    await localNer.ConfigureAwait(false);
                foreach (ClusterNerGiveUp give in ner.DrainGiveUps())
                {
                    _logger.LogWarning(
                        "Cluster job {JobId} NER group {Group} abandoned after {Attempts} attempts; pages [{Pages}]",
                        id,
                        give.GroupId,
                        give.Attempts,
                        give.Pages.Length == 0 ? "(none)" : string.Join(",", give.Pages));
                }

                if (localNerCap > 0 || ner.SawSuccessfulExtract)
                {
                    distributedEntities = ner.Merge();
                    usedDistributed = true;
                    _logger.LogInformation(
                        "LLM NER done: companies={Companies}, persons={Persons}",
                        distributedEntities.Companies.Count,
                        distributedEntities.Persons.Count);
                }
                else
                {
                    _logger.LogInformation(
                        "Cluster job {JobId} distributed NER had no LLM-capable node; coordinator will use the non-distributed path",
                        id);
                }
            }
        }
        finally
        {
            job.MarkFinished();
            await linked.CancelAsync().ConfigureAwait(false);
            await Quiet(notify).ConfigureAwait(false);
            await Quiet(grace).ConfigureAwait(false);
            await Quiet(deadline).ConfigureAwait(false);
            if (localNer is not null)
                await Quiet(localNer).ConfigureAwait(false);
            job.StopNewPdfReads();
            await job.WaitForPdfReadersAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            ClusterScheduleSnapshot snap = scheduler.Snapshot();
            var breakdown = new List<ClusterNodePages>();
            foreach (ClusterNodeLoad load in snap.Nodes.OrderBy(n => n.NodeId, StringComparer.Ordinal))
            {
                if (load.PagesCommitted <= 0)
                    continue;
                breakdown.Add(new ClusterNodePages
                {
                    NodeId = load.NodeId,
                    Pages = load.PagesCommitted,
                });
            }

            double elapsed = (DateTimeOffset.UtcNow - job.Started).TotalMilliseconds;
            string byNode = ClusterJobLog.FormatByNode(snap);
            string nerSuffix = "";
            int nerGroups = 0;
            List<ClusterNodePages>? nerByNode = null;
            if (ner is not null)
            {
                ClusterNerSnapshot nerSnap = ner.Snapshot();
                nerSuffix = " " + ClusterJobLog.FormatNer(nerSnap);
                nerGroups = nerSnap.Formed;
                nerByNode = [];
                foreach (ClusterNodeLoad load in nerSnap.Nodes)
                {
                    if (load.PagesCommitted <= 0)
                        continue;
                    nerByNode.Add(new ClusterNodePages { NodeId = load.NodeId, Pages = load.PagesCommitted });
                }
            }

            double pagesPerSecond = pageCount / Math.Max(0.001, elapsed / 1000.0);
            _logger.LogInformation(
                "Cluster job {JobId} done: pages={Pages} elapsedMs={Elapsed:F0} {Rate:F1} pages/s byNode={ByNode}{Ner}",
                id,
                pageCount,
                elapsed,
                pagesPerSecond,
                byNode,
                nerSuffix);

            var last = new ClusterLastJobHealth
            {
                JobId = id,
                PageCount = pageCount,
                ElapsedMs = Math.Round(elapsed, 1),
                Nodes = breakdown,
                NerGroups = nerGroups,
                NerByNode = nerByNode,
            };

            lock (_publish)
            {
                _registry.AddPages(snap.Nodes);
                _jobs.TryRemove(id, out _);
                _lastJob = last;
            }
        }

        return new ClusterRunResult(id, usedDistributed, distributedEntities);
    }

    public readonly record struct ClusterRunResult(string JobId, bool UsedDistributedNer, OcrEntities? Entities);

    /// <summary>How long <c>/ner/claim</c> stays open when no group is handed out. Workers block here instead of polling.</summary>
    public const int NerClaimWaitMs = 15_000;

    public async Task<ClusterNerClaimResponse> ClaimNerAsync(
        ClusterJob job,
        string nodeId,
        bool llmConfigured,
        int nerConcurrency,
        int waitMs,
        CancellationToken ct)
    {
        ClusterNerClaimResponse first = ClaimNerOnce(job, nodeId, llmConfigured, nerConcurrency);
        if (first.Done || !first.Wait || job.Ner is null || !llmConfigured || nerConcurrency <= 0)
            return first;

        int hold = waitMs <= 0 ? NerClaimWaitMs : Math.Clamp(waitMs, 1, 25_000);
        job.Ner.EnterWait(nodeId, DateTimeOffset.UtcNow);
        try
        {
            await job.Ner.WaitForChangeAsync(first.Version, TimeSpan.FromMilliseconds(hold), ct).ConfigureAwait(false);
        }
        finally
        {
            job.Ner.LeaveWait(nodeId);
        }

        return ClaimNerOnce(job, nodeId, llmConfigured, nerConcurrency);
    }

    private ClusterNerClaimResponse ClaimNerOnce(ClusterJob job, string nodeId, bool llmConfigured, int nerConcurrency)
    {
        if (job.IsFinished || job.Ner is null)
            return new ClusterNerClaimResponse { Done = true, Version = job.Ner?.Version ?? 0 };

        if (!llmConfigured || nerConcurrency <= 0)
        {
            job.Ner.SetNerCapacity(nodeId, 0);
            return job.Ner.IsComplete
                ? new ClusterNerClaimResponse { Done = true, Version = job.Ner.Version }
                : new ClusterNerClaimResponse { Wait = true, RetryAfterMs = 500, Version = job.Ner.Version };
        }

        job.Ner.SetNerCapacity(nodeId, nerConcurrency);
        ClusterNerAssignment claim = job.Ner.Claim(nodeId, DateTimeOffset.UtcNow);
        LogNerExpiries(job);
        if (claim.Kind == ClusterNerClaimKind.Done)
            return new ClusterNerClaimResponse { Done = true, Version = claim.Version };
        if (claim.Kind != ClusterNerClaimKind.Group)
            return new ClusterNerClaimResponse { Wait = true, RetryAfterMs = claim.RetryAfterMs, Version = claim.Version };

        ClusterJobLog.NerClaim(
            _logger,
            _config.VerboseDispatch,
            job.Id,
            nodeId,
            claim.GroupId,
            string.Join(",", claim.Pages),
            claim.LeaseMs);
        List<ClusterNerPageText> texts = new(claim.Bodies.Length);
        foreach (OcrPageResult page in claim.Bodies)
            texts.Add(new ClusterNerPageText { Page = page.Page, Text = page.Text ?? "" });
        return new ClusterNerClaimResponse
        {
            GroupId = claim.GroupId,
            Pages = claim.Pages.ToList(),
            PageTexts = texts,
            PromptText = claim.PromptText,
            Lookahead = claim.Lookahead,
            LookaheadPage = claim.LookaheadPage,
            LeaseMs = claim.LeaseMs,
            RetryAfterMs = claim.RetryAfterMs,
            Version = claim.Version,
        };
    }

    public int AcceptNer(ClusterJob job, ClusterNerResultRequest body)
    {
        if (job.Ner is null || string.IsNullOrWhiteSpace(body.GroupId))
            return 0;
        bool ok = job.Ner.TryComplete(body.GroupId, body.NodeId ?? "", body.Companies, body.Persons, body.Entities);
        if (!ok)
            return 0;
        ClusterJobLog.NerDone(
            _logger,
            _config.VerboseDispatch,
            body.NodeId ?? "",
            job.Id,
            body.GroupId,
            body.Entities?.Count ?? 0);
        MaybeLogProgress(job);
        return 1;
    }

    public void FailNer(ClusterJob job, string nodeId, string groupId, string? error)
    {
        if (job.Ner is null || string.IsNullOrWhiteSpace(groupId))
            return;
        job.Ner.Fail(groupId, nodeId);
        _logger.LogWarning(
            "Cluster job {JobId} requeued NER group {Group} from node {Node}: {Error}",
            job.Id,
            groupId,
            nodeId,
            string.IsNullOrWhiteSpace(error) ? "failed" : error);
        foreach (ClusterNerGiveUp give in job.Ner.DrainGiveUps())
        {
            _logger.LogWarning(
                "Cluster job {JobId} NER group {Group} abandoned after {Attempts} attempts; pages [{Pages}]",
                job.Id,
                give.GroupId,
                give.Attempts,
                give.Pages.Length == 0 ? "(none)" : string.Join(",", give.Pages));
        }
    }

    private async Task RunLocalNerAsync(ClusterJob job, ClusterNerScheduler ner, int localCap, CancellationToken ct)
    {
        if (localCap <= 0)
        {
            while (!ct.IsCancellationRequested)
            {
                if (ner.IsComplete)
                    return;
                if (ner.IsSealed && !ner.HasAnyCapacity)
                    return;
                await ner.WaitForChangeAsync(ner.Version, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }

            return;
        }

        Task[] workers = new Task[localCap];
        for (int i = 0; i < localCap; i++)
            workers[i] = LocalNerConsumerAsync(job, ner, ct);
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task LocalNerConsumerAsync(ClusterJob job, ClusterNerScheduler ner, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (ner.IsComplete)
                return;
            ClusterNerAssignment claim = ner.Claim(_config.NodeId, DateTimeOffset.UtcNow);
            LogNerExpiries(job);
            if (claim.Kind == ClusterNerClaimKind.Done)
                return;
            if (claim.Kind != ClusterNerClaimKind.Group)
            {
                DateTimeOffset waited = DateTimeOffset.UtcNow;
                ner.EnterWait(_config.NodeId, waited);
                try
                {
                    await ner.WaitForChangeAsync(claim.Version, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                finally
                {
                    ner.LeaveWait(_config.NodeId);
                }

                continue;
            }

            ClusterJobLog.NerClaim(
                _logger,
                _config.VerboseDispatch,
                job.Id,
                _config.NodeId,
                claim.GroupId,
                string.Join(",", claim.Pages),
                claim.LeaseMs);
            try
            {
                ClusterNerResultRequest result = await _llm.ExtractDistributedAsync(claim, ct).ConfigureAwait(false);
                result.NodeId = _config.NodeId;
                result.GroupId = claim.GroupId;
                bool ok = ner.TryComplete(claim.GroupId, _config.NodeId, result.Companies, result.Persons, result.Entities);
                if (ok)
                {
                    ClusterJobLog.NerDone(
                        _logger,
                        _config.VerboseDispatch,
                        _config.NodeId,
                        job.Id,
                        claim.GroupId,
                        result.Entities?.Count ?? 0);
                    MaybeLogProgress(job);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ner.Fail(claim.GroupId, _config.NodeId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Cluster job {JobId} local NER group {Group} failed; requeued",
                    job.Id,
                    claim.GroupId);
                ner.Fail(claim.GroupId, _config.NodeId);
                foreach (ClusterNerGiveUp give in ner.DrainGiveUps())
                {
                    _logger.LogWarning(
                        "Cluster job {JobId} NER group {Group} abandoned after {Attempts} attempts; pages [{Pages}]",
                        job.Id,
                        give.GroupId,
                        give.Attempts,
                        give.Pages.Length == 0 ? "(none)" : string.Join(",", give.Pages));
                }
            }
        }
    }

    private void NoteNerCapacity(string nodeId, bool? configured, int? concurrency)
    {
        if (configured is not bool on || string.IsNullOrWhiteSpace(nodeId))
            return;
        int cap = on && concurrency is int n && n > 0 ? Math.Clamp(n, 1, 32) : 0;
        foreach (ClusterJob job in _jobs.Values)
            job.Ner?.SetNerCapacity(nodeId, cap);
    }

    private void WarnModelMismatch(List<ClusterRemote> remotes, int jobDpi)
    {
        foreach (ClusterRemote remote in remotes)
        {
            if (!string.IsNullOrWhiteSpace(remote.Warning))
            {
                _logger.LogWarning(
                    "Cluster node {NodeId} ({Url}) differs from coordinator ({Warning}). OCR text may not match single-node output.",
                    remote.NodeId,
                    remote.Url,
                    remote.Warning);
            }

            if (remote.Dpi > 0 && remote.Dpi != jobDpi)
            {
                _logger.LogWarning(
                    "Cluster node {NodeId} configured dpi={NodeDpi} differs from job dpi={JobDpi}. This job still renders at {JobDpi} on every node.",
                    remote.NodeId,
                    remote.Dpi,
                    jobDpi,
                    jobDpi);
            }
        }
    }

    private async Task HoldGraceAsync(ClusterJob job, bool expectRemote, CancellationToken ct)
    {
        if (!expectRemote || _config.JoinGraceMs <= 0)
        {
            job.Scheduler.SetHoldLocalWindow(false);
            return;
        }

        try
        {
            await Task.Delay(_config.JoinGraceMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        job.MarkGraceElapsed();
        bool stillDownloading = job.ShouldHoldLocal(DateTimeOffset.UtcNow);
        job.Scheduler.SetHoldLocalWindow(stillDownloading);
        _logger.LogInformation(
            stillDownloading
                ? "Cluster job {JobId} join grace ({GraceMs}ms) elapsed; holding the local window while a node downloads the PDF"
                : "Cluster job {JobId} join grace ({GraceMs}ms) elapsed; local node may take remaining pages",
            job.Id,
            _config.JoinGraceMs);
    }

    private async Task DeadlineAsync(ClusterJob job, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_config.JobDeadlineMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogWarning(
            "Cluster job {JobId} hit deadline ({DeadlineMs}ms); dropping remote leases so the local node can finish",
            job.Id,
            _config.JobDeadlineMs);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        job.ForceReleaseDownloadHold();
        job.Scheduler.SetHoldLocalWindow(false);
        job.Scheduler.TakeOverLocal(now);
        if (job.Ner is null)
            return;
        if (job.Ner.LocalCanRun)
        {
            job.Ner.TakeOverLocal(now);
            _logger.LogWarning(
                "Cluster job {JobId} NER leases moved to the local node",
                job.Id);
        }
        else
        {
            int abandoned = job.Ner.AbandonRemaining();
            _logger.LogWarning(
                "Cluster job {JobId} deadline with no local LLM; abandoned {Groups} NER groups",
                job.Id,
                abandoned);
        }
    }

    private async Task NotifyWorkersAsync(
        ClusterJob job,
        List<ClusterRemote> remotes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.AdvertiseUrl))
        {
            if (remotes.Count > 0)
            {
                _logger.LogWarning(
                    "Cluster advertiseUrl is empty; static workers will not be notified. Workers that poll coordinatorUrl can still join job {JobId}.",
                    job.Id);
            }

            return;
        }

        HttpClient http = _httpFactory.CreateClient(HttpClientName);
        var body = new ClusterNotifyRequest
        {
            JobId = job.Id,
            CoordinatorUrl = _config.AdvertiseUrl,
            Dpi = job.Dpi,
            PageCount = job.PageCount,
            SourceUrl = string.IsNullOrWhiteSpace(job.SourceUrl) ? null : job.SourceUrl,
        };

        await Task.WhenAll(remotes.Select(remote => NotifyOneAsync(http, job, remote, body, ct)))
            .ConfigureAwait(false);
    }

    private async Task NotifyOneAsync(
        HttpClient http,
        ClusterJob job,
        ClusterRemote remote,
        ClusterNotifyRequest body,
        CancellationToken ct)
    {
        string url = remote.Url + "/cluster/notify";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            AddAuth(req);
            req.Content = JsonContent.Create(body, AppJsonContext.Default.ClusterNotifyRequest);
            using HttpResponseMessage resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Cluster notify {Url} for job {JobId} returned {Status}. The worker can still poll.",
                    url,
                    job.Id,
                    (int)resp.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Cluster notify {Url} for job {JobId} failed. The worker can still poll coordinatorUrl.",
                url,
                job.Id);
        }
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.HealthIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await ProbeOnceAsync(ct).ConfigureAwait(false);
                await SyncNacosNodesAsync(ct).ConfigureAwait(false);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (ClusterJob job in _jobs.Values)
                {
                    if (job.ExpectRemote)
                        job.Scheduler.SetHoldLocalWindow(job.ShouldHoldLocal(now));
                    job.Scheduler.Reap(now);
                    job.Ner?.Reap(now);
                    LogExpiries(job);
                    LogNerExpiries(job);
                    MaybeLogProgress(job);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        List<ClusterRemote> remotes = _registry.Remotes();
        if (remotes.Count == 0)
            return;
        HttpClient http = _httpFactory.CreateClient(HttpClientName);
        foreach (ClusterRemote remote in remotes)
        {
            ClusterInfoResponse? info = null;
            bool ok = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var req = new HttpRequestMessage(HttpMethod.Get, remote.Url + "/cluster/info");
                AddAuth(req);
                using HttpResponseMessage resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    await using Stream stream = await resp.Content.ReadAsStreamAsync(timeout.Token)
                        .ConfigureAwait(false);
                    info = await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.ClusterInfoResponse, timeout.Token)
                        .ConfigureAwait(false);
                    ok = info is not null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException or JsonException)
            {
                ok = false;
            }

            bool becameUnhealthy = _registry.NoteProbe(remote.NodeId, ok, info);
            if (ok && info is not null)
                NoteNerCapacity(remote.NodeId, info.LlmConfigured, info.NerConcurrency);
            if (!becameUnhealthy)
                continue;

            _logger.LogWarning(
                "Cluster node {NodeId} at {Url} is unhealthy; its leased pages will be retried elsewhere",
                remote.NodeId,
                remote.Url);
            foreach (ClusterJob job in _jobs.Values)
            {
                job.Scheduler.DropNode(remote.NodeId, DateTimeOffset.UtcNow);
                job.Ner?.DropNode(remote.NodeId, DateTimeOffset.UtcNow);
            }
        }
    }

    private async Task SyncNacosNodesAsync(CancellationToken ct)
    {
        if (_nacos is null || !_config.UseNacos)
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            List<NacosInstance> instances = await _nacos.ListInstancesAsync(timeout.Token).ConfigureAwait(false);

            var activeIds = new HashSet<string>(StringComparer.Ordinal);

            if (instances.Count > 0)
            {
                foreach (NacosInstance instance in instances)
                {
                    if (!instance.Healthy || !instance.Enabled)
                        continue;

                    string nodeId = instance.Metadata?.GetValueOrDefault("nodeId") ?? instance.InstanceId ?? "";
                    if (string.IsNullOrWhiteSpace(nodeId))
                        continue;

                    activeIds.Add(nodeId);

                    // Skip self.
                    if (string.Equals(nodeId, _config.NodeId, StringComparison.Ordinal))
                        continue;

                    string url = NacosClient.ResolveUrl(instance);
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    string ocrMode = instance.Metadata?.GetValueOrDefault("ocrMode") ?? "";
                    string model = instance.Metadata?.GetValueOrDefault("model") ?? "";
                    int dpi = int.TryParse(instance.Metadata?.GetValueOrDefault("dpi"), out int pd) ? pd : 0;
                    int capacity = int.TryParse(instance.Metadata?.GetValueOrDefault("capacity"), out int pc) ? pc : 1;
                    int engineCount = int.TryParse(instance.Metadata?.GetValueOrDefault("engineCount"), out int pe) ? pe : 0;
                    bool? llmConfigured = null;
                    int? nerConcurrency = null;
                    string? llmRaw = instance.Metadata?.GetValueOrDefault("llmConfigured");
                    if (!string.IsNullOrWhiteSpace(llmRaw))
                    {
                        llmConfigured = llmRaw is "1" or "true" or "True" or "yes";
                        nerConcurrency = int.TryParse(instance.Metadata?.GetValueOrDefault("nerConcurrency"), out int nc) ? nc : 0;
                    }

                    var req = new ClusterRegisterRequest
                    {
                        NodeId = nodeId,
                        BaseUrl = url,
                        Capacity = Math.Max(1, capacity),
                        OcrMode = ocrMode,
                        Model = model,
                        Dpi = dpi,
                        EngineCount = engineCount,
                        LlmConfigured = llmConfigured,
                        NerConcurrency = nerConcurrency,
                    };

                    string? warning = _registry.Register(req);
                    _registry.MarkNacosSeen(nodeId);
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        _logger.LogDebug(
                            "Nacos node {NodeId} at {Url} differs from coordinator ({Warning}). Page text may differ across machines.",
                            nodeId,
                            url,
                            warning);
                    }

                    // Also update capacity on active jobs.
                    foreach (ClusterJob job in _jobs.Values)
                        job.Scheduler.SetCapacity(nodeId, Math.Max(1, capacity));
                    NoteNerCapacity(nodeId, llmConfigured, nerConcurrency);
                }
            }

            // Prune nodes that haven't been seen in 3 poll cycles.
            _registry.PruneStaleNacosNodes(
                TimeSpan.FromMilliseconds(_config.HealthIntervalMs * 3 + 2000),
                activeIds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nacos sync nodes failed");
        }
    }

    private void LogClaim(string jobId, string nodeId, ClusterClaim claim)
    {
        if (claim.Kind != ClusterClaimKind.Batch)
            return;
        ClusterJobLog.Claim(
            _logger,
            _config.VerboseDispatch,
            jobId,
            nodeId,
            claim.BatchId,
            string.Join(",", claim.Pages),
            claim.Speculative,
            claim.LeaseMs);
    }

    private void LogExpiries(ClusterJob job)
    {
        foreach (ClusterLeaseExpiry expiry in job.Scheduler.DrainExpiries())
        {
            string pages = expiry.Pages.Length == 0 ? "(none)" : string.Join(",", expiry.Pages);
            ClusterJobLog.LeaseExpired(_logger, job.Id, expiry.NodeId, expiry.BatchId, pages, expiry.Speculative);
        }
    }

    private void MaybeLogProgress(ClusterJob job)
    {
        if (job.IsFinished)
            return;
        lock (job.ProgressGate)
            MaybeLogProgressCore(job);
    }

    private void MaybeLogProgressCore(ClusterJob job)
    {
        ClusterScheduleSnapshot snap = job.Scheduler.Snapshot();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ClusterJobLog.ProgressDecision decision = ClusterJobLog.EvaluateProgress(
            snap.Done,
            job.PageCount,
            job.Started,
            job.LastProgressDone,
            job.LastProgressBucket,
            job.LastProgressAt,
            now);
        string? nerText = null;
        bool nerLog = false;
        if (job.Ner is not null)
        {
            ClusterNerSnapshot ner = job.Ner.Snapshot();
            if (ner.Formed > 0)
            {
                ClusterJobLog.ProgressDecision nerDecision = ClusterJobLog.EvaluateProgress(
                    ner.Done,
                    ner.Formed,
                    job.Started,
                    job.LastNerDone,
                    job.LastNerBucket,
                    job.LastProgressAt,
                    now);
                nerLog = nerDecision.Log;
                if (decision.Log || nerLog)
                {
                    nerText = ClusterJobLog.FormatNer(ner);
                    if (nerLog)
                    {
                        job.LastNerDone = ner.Done;
                        job.LastNerBucket = nerDecision.Bucket;
                    }
                }
            }
        }

        if (!decision.Log && !nerLog)
            return;
        if (decision.Log)
        {
            job.LastProgressDone = snap.Done;
            job.LastProgressBucket = decision.Bucket;
        }

        job.LastProgressAt = now;
        ClusterJobLog.Progress(
            _logger,
            job.Id,
            snap.Done,
            job.PageCount,
            decision.Percent,
            decision.PagesPerSecond,
            snap.LeasedPages,
            ClusterJobLog.FormatByNode(snap),
            nerText);
    }

    private void LogNerExpiries(ClusterJob job)
    {
        if (job.Ner is null)
            return;
        foreach (ClusterLeaseExpiry expiry in job.Ner.DrainExpiries())
        {
            string pages = expiry.Pages.Length == 0 ? "(none)" : string.Join(",", expiry.Pages);
            ClusterJobLog.LeaseExpired(_logger, job.Id, expiry.NodeId, expiry.BatchId, pages, expiry.Speculative);
        }
    }

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
    }

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
