using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>Authenticated cluster routes. Mapped only when clustering is enabled.</summary>
public static partial class ClusterEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdPattern();

    public static void Map(WebApplication app)
    {
        app.MapGet("/cluster/info", (HttpRequest http, ClusterRuntimeConfig cfg, ClusterSelf self, ClusterWorkerHost worker) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            return Results.Json(worker.Info(), AppJsonContext.Relaxed.ClusterInfoResponse);
        });

        app.MapPost("/cluster/register", async Task<IResult> (
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!cfg.IsCoordinator)
                return Ack(StatusCodes.Status409Conflict, "This node is not a coordinator.");

            ClusterRegisterRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterRegisterRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.NodeId))
                return Ack(StatusCodes.Status400BadRequest, "nodeId is required.");

            string? warning;
            try
            {
                warning = coordinator.Register(body);
            }
            catch (ArgumentException ex)
            {
                return Ack(StatusCodes.Status400BadRequest, ex.Message);
            }

            return Results.Json(
                new ClusterRegisterResponse
                {
                    Ok = true,
                    Warning = warning,
                    OcrMode = coordinator.BuildHealth().OcrMode,
                    Model = coordinator.BuildHealth().Model,
                    Dpi = coordinator.BuildHealth().Dpi,
                },
                AppJsonContext.Relaxed.ClusterRegisterResponse);
        });

        app.MapPost("/cluster/heartbeat", async Task<IResult> (
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!cfg.IsCoordinator)
                return Ack(StatusCodes.Status409Conflict, "This node is not a coordinator.");
            ClusterHeartbeatRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterHeartbeatRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.NodeId))
                return Ack(StatusCodes.Status400BadRequest, "nodeId is required.");
            coordinator.Heartbeat(body);
            return Ack(StatusCodes.Status200OK, null);
        });

        app.MapPost("/cluster/dispatch", async Task<IResult> (
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!cfg.IsCoordinator)
                return Ack(StatusCodes.Status409Conflict, "This node is not a coordinator.");
            ClusterDispatchRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterDispatchRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            body ??= new ClusterDispatchRequest();
            ClusterDispatchResponse response = coordinator.Dispatch(
                body.NodeId ?? "",
                body.Capacity,
                body.ActiveJobs,
                body.ActiveSessions);
            return Results.Json(response, AppJsonContext.Relaxed.ClusterDispatchResponse);
        });

        app.MapPost("/cluster/notify", async Task<IResult> (
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterWorkerHost worker,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            ClusterNotifyRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterNotifyRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null)
                return Ack(StatusCodes.Status400BadRequest, "Empty body.");
            if (!worker.TryStartNotified(body, out string? error))
            {
                int code = error is not null && error.Contains("capacity", StringComparison.OrdinalIgnoreCase)
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status409Conflict;
                return Ack(code, error);
            }

            return Ack(StatusCodes.Status200OK, null);
        });

        app.MapMethods("/cluster/jobs/{jobId}/pdf", [HttpMethods.Get, HttpMethods.Head], (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;

            int length = job!.PdfLength;
            bool head = HttpMethods.IsHead(http.Method);
            long start = 0;
            long end = length - 1;
            bool partial = false;
            RangeHeaderValue? range = http.GetTypedHeaders().Range;
            if (range is { Ranges.Count: 1 })
            {
                RangeItemHeaderValue item = range.Ranges.First();
                if (!ClusterPdfRange.TrySlice(length, item.From, item.To, out start, out end))
                    return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
                partial = true;
            }

            ClusterJob.PdfReadLease? lease = null;
            if (!head)
            {
                lease = job.TryEnterPdfRead();
                if (lease is null)
                    return Ack(StatusCodes.Status409Conflict, "Job is closing.");
            }

            return new PdfBytesResult(job.Pdf, length, start, end, partial, head, lease);
        });

        app.MapPost("/cluster/jobs/{jobId}/join", async Task<IResult> (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;
            ClusterJoinRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterJoinRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.NodeId))
                return Ack(StatusCodes.Status400BadRequest, "nodeId is required.");
            if (!coordinator.Join(job!, body.NodeId.Trim(), body.Capacity, body.Downloading))
                return Ack(StatusCodes.Status409Conflict, "Job is finished.");
            return Ack(StatusCodes.Status200OK, null);
        });

        app.MapPost("/cluster/jobs/{jobId}/claim", async Task<IResult> (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;
            ClusterClaimRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterClaimRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.NodeId))
                return Ack(StatusCodes.Status400BadRequest, "nodeId is required.");
            ClusterClaimResponse claim = coordinator.Claim(job!, body.NodeId.Trim(), body.MaxPages);
            return Results.Json(claim, AppJsonContext.Relaxed.ClusterClaimResponse);
        });

        app.MapPost("/cluster/jobs/{jobId}/result", async Task<IResult> (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;
            ClusterResultRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterResultRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.BatchId))
                return Ack(StatusCodes.Status400BadRequest, "batchId is required.");
            int accepted = coordinator.AcceptResults(job!, body.BatchId, body.Pages);
            return Results.Json(
                new ClusterAck { Ok = true, Accepted = accepted },
                AppJsonContext.Relaxed.ClusterAck);
        });

        app.MapPost("/cluster/jobs/{jobId}/fail", async Task<IResult> (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;
            ClusterFailRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterFailRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.BatchId))
                return Ack(StatusCodes.Status400BadRequest, "batchId is required.");
            coordinator.FailBatch(job!, body.NodeId ?? "", body.BatchId, body.Error);
            return Ack(StatusCodes.Status200OK, null);
        });

        app.MapPost("/cluster/jobs/{jobId}/ner/claim", async Task<IResult> (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;
            ClusterNerClaimRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterNerClaimRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.NodeId))
                return Ack(StatusCodes.Status400BadRequest, "nodeId is required.");
            ClusterNerClaimResponse claim = await coordinator.ClaimNerAsync(
                job!,
                body.NodeId.Trim(),
                body.LlmConfigured,
                body.NerConcurrency,
                body.WaitMs,
                ct).ConfigureAwait(false);
            return Results.Json(claim, AppJsonContext.Relaxed.ClusterNerClaimResponse);
        });

        app.MapPost("/cluster/jobs/{jobId}/ner/result", async Task<IResult> (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;
            ClusterNerResultRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterNerResultRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.GroupId))
                return Ack(StatusCodes.Status400BadRequest, "groupId is required.");
            int accepted = coordinator.AcceptNer(job!, body);
            return Results.Json(
                new ClusterAck { Ok = true, Accepted = accepted },
                AppJsonContext.Relaxed.ClusterAck);
        });

        app.MapPost("/cluster/jobs/{jobId}/ner/fail", async Task<IResult> (
            string jobId,
            HttpRequest http,
            ClusterRuntimeConfig cfg,
            ClusterCoordinator coordinator,
            CancellationToken ct) =>
        {
            if (!Authorize(http, cfg, out IResult? deny))
                return deny!;
            if (!TryJob(coordinator, cfg, jobId, out ClusterJob? job, out IResult? missing))
                return missing!;
            ClusterFailRequest? body;
            try
            {
                body = await http.ReadFromJsonAsync(AppJsonContext.Relaxed.ClusterFailRequest, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Ack(StatusCodes.Status400BadRequest, "Invalid JSON body.");
            }

            if (body is null || string.IsNullOrWhiteSpace(body.BatchId))
                return Ack(StatusCodes.Status400BadRequest, "batchId is required.");
            coordinator.FailNer(job!, body.NodeId ?? "", body.BatchId, body.Error);
            return Ack(StatusCodes.Status200OK, null);
        });
    }

    private static bool Authorize(HttpRequest http, ClusterRuntimeConfig cfg, out IResult? deny)
    {
        if (ClusterAuth.IsAuthorized(http, cfg.Token))
        {
            deny = null;
            return true;
        }

        deny = Results.Json(
            new ClusterAck { Ok = false, Error = "Unauthorized." },
            AppJsonContext.Relaxed.ClusterAck,
            statusCode: StatusCodes.Status401Unauthorized);
        return false;
    }

    private static bool TryJob(
        ClusterCoordinator coordinator,
        ClusterRuntimeConfig cfg,
        string jobId,
        out ClusterJob? job,
        out IResult? error)
    {
        job = null;
        error = null;
        if (!cfg.IsCoordinator)
        {
            error = Ack(StatusCodes.Status409Conflict, "This node is not a coordinator.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(jobId) || !JobIdPattern().IsMatch(jobId))
        {
            error = Ack(StatusCodes.Status400BadRequest, "Invalid job id.");
            return false;
        }

        job = coordinator.FindJob(jobId);
        if (job is null)
        {
            error = Ack(StatusCodes.Status404NotFound, "Unknown job.");
            return false;
        }

        return true;
    }

    private static IResult Ack(int status, string? error) =>
        Results.Json(
            new ClusterAck { Ok = status is >= 200 and < 300, Error = error },
            AppJsonContext.Relaxed.ClusterAck,
            statusCode: status);

    /// <summary>
    /// Serves the job PDF with Content-Length and Accept-Ranges. A single byte range is 206.
    /// The read lease's token is cancelled when the job finishes so the body does not hold completion.
    /// </summary>
    private sealed class PdfBytesResult : IResult
    {
        private readonly byte[] _pdf;
        private readonly int _length;
        private readonly long _start;
        private readonly long _end;
        private readonly bool _partial;
        private readonly bool _head;
        private readonly ClusterJob.PdfReadLease? _lease;

        public PdfBytesResult(
            byte[] pdf,
            int length,
            long start,
            long end,
            bool partial,
            bool head,
            ClusterJob.PdfReadLease? lease)
        {
            _pdf = pdf;
            _length = length;
            _start = start;
            _end = end;
            _partial = partial;
            _head = head;
            _lease = lease;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            HttpResponse response = httpContext.Response;
            int count = checked((int)(_end - _start + 1));
            response.StatusCode = _partial
                ? StatusCodes.Status206PartialContent
                : StatusCodes.Status200OK;
            response.ContentType = "application/pdf";
            response.ContentLength = count;
            response.Headers.AcceptRanges = "bytes";
            if (_partial)
                response.Headers.ContentRange = $"bytes {_start}-{_end}/{_length}";
            if (_head)
                return;

            ClusterJob.PdfReadLease? lease = _lease;
            try
            {
                await response.Body.WriteAsync(
                    _pdf.AsMemory((int)_start, count),
                    lease?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }
}
