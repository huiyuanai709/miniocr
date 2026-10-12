using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Accepts challenge jobs into a bounded channel, processes them asynchronously
/// (download → OCR → map → callback), with a small concurrency cap (~5 QPM friendly).
/// Files in one request share the GPU one at a time: OCR of the next PDF starts when
/// the previous PDF's OCR finishes, while that file's NER keeps running. The callback
/// waits until every file's OCR and NER are done.
/// </summary>
public sealed class ChallengeJobService : IHostedService, IDisposable
{
    public const string CallbackHttpClientName = "ChallengeCallback";

    /// <summary>Max concurrent OCR jobs (keeps memory/CPU sane under a few parallel platform calls).</summary>
    private const int MaxConcurrentJobs = 2;

    private readonly Channel<ChallengeJob> _channel;
    private readonly ParallelPdfDownloader _downloader;
    private readonly PdfOcrPipeline _pipeline;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChallengeJobService> _logger;
    private readonly ClusterCoordinator? _cluster;
    private readonly OcrTraceLog? _trace;
    private readonly string _resourceDir;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private readonly SemaphoreSlim _jobGate = new(MaxConcurrentJobs, MaxConcurrentJobs);

    public ChallengeJobService(
        ParallelPdfDownloader downloader,
        PdfOcrPipeline pipeline,
        IHttpClientFactory httpClientFactory,
        ILogger<ChallengeJobService> logger,
        ClusterCoordinator? cluster = null,
        OcrTraceLog? trace = null,
        ChallengeRuntimeConfig? challenge = null)
    {
        _downloader = downloader;
        _pipeline = pipeline;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cluster = cluster;
        _trace = trace;
        _resourceDir = challenge?.ResourceDir ?? "";
        _channel = Channel.CreateBounded<ChallengeJob>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public int QueuedApprox => _channel.Reader.Count;

    public bool TryEnqueue(ChallengeJob job) => _channel.Writer.TryWrite(job);

    public ValueTask EnqueueAsync(ChallengeJob job, CancellationToken ct) =>
        _channel.Writer.WriteAsync(job, ct);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation(
            "Challenge job worker started (maxConcurrent={MaxConcurrent}, queueCapacity=32)",
            MaxConcurrentJobs);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        List<Task> inFlight = [];
        try
        {
            await foreach (ChallengeJob job in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _jobGate.WaitAsync(ct).ConfigureAwait(false);
                Task t = ProcessGuardedAsync(job, ct);
                inFlight.Add(t);
                inFlight.RemoveAll(static x => x.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(inFlight).ConfigureAwait(false);
            }
            catch
            {
                // Individual failures already logged.
            }
        }
    }

    private async Task ProcessGuardedAsync(ChallengeJob job, CancellationToken ct)
    {
        try
        {
            await ProcessJobAsync(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Challenge job failed: teamId={TeamId}, keyPresent={KeyPresent}, files={FileCount}",
                job.TeamId,
                !string.IsNullOrEmpty(job.Key),
                job.Files.Count);
        }
        finally
        {
            _jobGate.Release();
        }
    }

    private async Task ProcessJobAsync(ChallengeJob job, CancellationToken ct)
    {
        _logger.LogInformation(
            "Challenge job start: teamId={TeamId}, keyPresent={KeyPresent}, files={FileCount}, callbackHost={Host}",
            job.TeamId,
            !string.IsNullOrEmpty(job.Key),
            job.Files.Count,
            TryHost(job.CallbackUrl));

        int count = job.Files.Count;
        ChallengeFileResult[] results = new ChallengeFileResult[count];
        for (int i = 0; i < count; i++)
            results[i] = new ChallengeFileResult { FileId = job.Files[i].FileId ?? "", Pages = [] };

        await SequentialPdfOverlap.RunAsync(
            count,
            (index, token) => StartFileAsync(job, index, results, token),
            continueAfterFailure: true,
            ct).ConfigureAwait(false);

        ChallengeCallbackBody body = new()
        {
            TeamId = job.TeamId,
            Key = job.Key,
            Result = [.. results],
        };

        await PostCallbackWithRetryAsync(job.CallbackUrl, body, ct).ConfigureAwait(false);
    }

    private async Task<SequentialPdfOverlap.Started?> StartFileAsync(
        ChallengeJob job,
        int index,
        ChallengeFileResult[] results,
        CancellationToken ct)
    {
        ChallengeFileRef file = job.Files[index];
        string fileId = file.FileId ?? "";
        ChallengeResourceFiles.SourceDecision decision =
            ChallengeResourceFiles.Decide(_resourceDir, file.FileId, file.Url);
        if (decision.Kind == ChallengeResourceFiles.SourceKind.Missing)
        {
            _logger.LogWarning(
                "Challenge file has no PDF: fileId={FileId}, path={Path}, downloadMode={Mode}",
                fileId,
                decision.LocalPath,
                _resourceDir.Length > 0 ? ChallengeResourceFiles.DownloadMode : "url");
            results[index] = new ChallengeFileResult { FileId = fileId, Pages = [] };
            return null;
        }

        ParallelPdfDownloader.DownloadResult download;
        string? sourceUrl;
        try
        {
            if (decision.Kind == ChallengeResourceFiles.SourceKind.Local)
            {
                _logger.LogInformation(
                    "Challenge file local resource: fileId={FileId}, path={Path}, downloadMode={Mode}",
                    fileId,
                    decision.LocalPath,
                    ChallengeResourceFiles.DownloadMode);
                ChallengeResourceFiles.LocalRead local =
                    ChallengeResourceFiles.ReadLocal(decision.LocalPath!);
                download = new ParallelPdfDownloader.DownloadResult(
                    local.Buffer, local.Mode, local.ElapsedMs);
                // Path lets cluster workers open the same file. It is not an http URL, so no prefetch.
                sourceUrl = decision.LocalPath;
            }
            else
            {
                string url = decision.Url!;
                if (decision.LocalPath is not null)
                {
                    _logger.LogInformation(
                        "Challenge file not in resource dir, downloading: fileId={FileId}, path={Path}, downloadMode=url-fallback",
                        fileId,
                        decision.LocalPath);
                }

                _cluster?.BeginSourcePrefetch(url);
                download = await _downloader.DownloadAsync(url, ct).ConfigureAwait(false);
                sourceUrl = url;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Challenge file failed: fileId={FileId}, path={Path}", fileId, decision.LocalPath);
            results[index] = new ChallengeFileResult { FileId = fileId, Pages = [] };
            return null;
        }

        string requestId = string.IsNullOrEmpty(job.RequestId) ? "" : job.RequestId;
        string entry = string.IsNullOrEmpty(job.TraceEntry) ? "challenge" : job.TraceEntry;
        IDisposable? traceScope = _trace is { Enabled: true }
            ? OcrTraceLog.BeginScope(requestId, entry, fileId, job.TeamId)
            : null;
        PdfOcrRun run;
        try
        {
            run = _pipeline.Start(download.Buffer, download.ElapsedMs, download.Mode, ct, sourceUrl: sourceUrl);
        }
        catch
        {
            traceScope?.Dispose();
            download.Buffer.Dispose();
            throw;
        }

        Task finished = FinishFileAsync(
            job, index, fileId, decision, download.Buffer, download.Mode, traceScope, run, results);
        return new SequentialPdfOverlap.Started(run.OcrCompleted, finished);
    }

    private async Task FinishFileAsync(
        ChallengeJob job,
        int index,
        string fileId,
        ChallengeResourceFiles.SourceDecision decision,
        RentedBuffer buffer,
        string downloadMode,
        IDisposable? traceScope,
        PdfOcrRun run,
        ChallengeFileResult[] results)
    {
        try
        {
            OcrResponse ocr = await run.Response.ConfigureAwait(false);
            if (_trace is { Enabled: true })
            {
                string requestId = string.IsNullOrEmpty(job.RequestId) ? "" : job.RequestId;
                string entry = string.IsNullOrEmpty(job.TraceEntry) ? "challenge" : job.TraceEntry;
                _trace.Text(
                    entry, requestId, fileId, decision.Url, ocr.PageCount, OcrTracePages.Copy(ocr.Pages),
                    job.TeamId, downloadMode, path: decision.LocalPath);
            }

            ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(fileId, ocr);
            results[index] = mapped;
            _logger.LogInformation(
                "Challenge file done: fileId={FileId}, path={Path}, pages={Pages}, downloadMode={Mode}",
                fileId,
                decision.LocalPath,
                mapped.Pages.Count,
                downloadMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Challenge file failed: fileId={FileId}, path={Path}", fileId, decision.LocalPath);
            results[index] = new ChallengeFileResult { FileId = fileId, Pages = [] };
        }
        finally
        {
            traceScope?.Dispose();
            buffer.Dispose();
        }
    }

    private async Task PostCallbackWithRetryAsync(
        string callbackUrl,
        ChallengeCallbackBody body,
        CancellationToken ct)
    {
        const int maxAttempts = 4;
        HttpClient http = _httpClientFactory.CreateClient(CallbackHttpClientName);
        Exception? last = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                    body, AppJsonContext.Relaxed.ChallengeCallbackBody);
                using ByteArrayContent content = new(json);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8",
                };
                using HttpResponseMessage resp = await http
                    .PostAsync(callbackUrl, content, ct)
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Challenge callback OK: attempt={Attempt}, status={Status}, host={Host}, keyPresent={KeyPresent}, bytes={Bytes}",
                        attempt,
                        (int)resp.StatusCode,
                        TryHost(callbackUrl),
                        !string.IsNullOrEmpty(body.Key),
                        json.Length);
                    return;
                }

                string snippet = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (snippet.Length > 200)
                    snippet = snippet[..200] + "…";
                _logger.LogWarning(
                    "Challenge callback non-success: attempt={Attempt}, status={Status}, body={Body}",
                    attempt,
                    (int)resp.StatusCode,
                    snippet);
                last = new HttpRequestException($"Callback HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                last = ex;
                _logger.LogWarning(
                    ex,
                    "Challenge callback error: attempt={Attempt}, host={Host}",
                    attempt,
                    TryHost(callbackUrl));
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }

            int delayMs = 500 * attempt * attempt;
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        _logger.LogError(
            last,
            "Challenge callback failed after retries: host={Host}, keyPresent={KeyPresent}",
            TryHost(callbackUrl),
            !string.IsNullOrEmpty(body.Key));
    }

    private static string TryHost(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return uri.Host;
        return "(invalid)";
    }

    public void Dispose()
    {
        _cts.Dispose();
        _jobGate.Dispose();
    }
}

public sealed class ChallengeJob
{
    public required int TeamId { get; init; }
    public required string Key { get; init; }
    public required string CallbackUrl { get; init; }
    public required List<ChallengeFileRef> Files { get; init; }
    /// <summary>Correlates the accept-time URL line with the OCR text line. Empty for older callers.</summary>
    public string RequestId { get; init; } = "";
    /// <summary><c>challenge</c> or <c>root</c>, matching the HTTP path that accepted the job.</summary>
    public string TraceEntry { get; init; } = "challenge";
}
