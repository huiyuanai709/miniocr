using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Accepts challenge jobs into a bounded channel, processes them asynchronously
/// (download → OCR → map → callback), with a small concurrency cap (~5 QPM friendly).
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
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private readonly SemaphoreSlim _jobGate = new(MaxConcurrentJobs, MaxConcurrentJobs);

    public ChallengeJobService(
        ParallelPdfDownloader downloader,
        PdfOcrPipeline pipeline,
        IHttpClientFactory httpClientFactory,
        ILogger<ChallengeJobService> logger,
        ClusterCoordinator? cluster = null)
    {
        _downloader = downloader;
        _pipeline = pipeline;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cluster = cluster;
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

        List<ChallengeFileResult> results = [];

        foreach (ChallengeFileRef file in job.Files)
        {
            string fileId = file.FileId ?? "";
            string? url = file.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("Skipping file with empty url: fileId={FileId}", fileId);
                results.Add(new ChallengeFileResult { FileId = fileId, Pages = [] });
                continue;
            }

            try
            {
                _cluster?.BeginSourcePrefetch(url);
                ParallelPdfDownloader.DownloadResult download =
                    await _downloader.DownloadAsync(url, ct).ConfigureAwait(false);
                using (download.Buffer)
                {
                    OcrResponse ocr = await _pipeline
                        .ProcessAsync(download.Buffer, download.ElapsedMs, download.Mode, ct, sourceUrl: url)
                        .ConfigureAwait(false);
                    ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(fileId, ocr);
                    results.Add(mapped);
                    _logger.LogInformation(
                        "Challenge file done: fileId={FileId}, pages={Pages}, downloadMode={Mode}",
                        fileId,
                        mapped.Pages.Count,
                        download.Mode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Challenge file failed: fileId={FileId}", fileId);
                results.Add(new ChallengeFileResult { FileId = fileId, Pages = [] });
            }
        }

        ChallengeCallbackBody body = new()
        {
            TeamId = job.TeamId,
            Key = job.Key,
            Result = results,
        };

        await PostCallbackWithRetryAsync(job.CallbackUrl, body, ct).ConfigureAwait(false);
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
}
