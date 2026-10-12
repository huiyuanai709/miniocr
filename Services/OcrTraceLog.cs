using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiniOcr.Services;

/// <summary>One OCR page handed to <see cref="OcrTraceLog"/> without copying the character data.</summary>
public readonly record struct OcrTracePage(int Page, string Source, string Text);

/// <summary>
/// Async JSONL trace of incoming file URLs and the OCR text for that request.
/// Callers only <c>TryWrite</c> a bounded queue. <c>TryWrite</c> returns immediately when the
/// queue is full (<see cref="BoundedChannelFullMode.Wait"/> blocks <c>WriteAsync</c>, not <c>TryWrite</c>).
/// A single background reader appends the file. A full queue or a writer failure increments
/// <see cref="Dropped"/> and never blocks OCR, cluster dispatch, or the HTTP accept path.
/// </summary>
public sealed class OcrTraceLog : IHostedService, IDisposable
{
    public const string EnabledEnv = "MINIOCR_OCR_TRACE";
    public const string PathEnv = "MINIOCR_OCR_TRACE_PATH";
    public const string MaxCharsEnv = "MINIOCR_OCR_TRACE_MAX_CHARS";
    public const string QueueEnv = "MINIOCR_OCR_TRACE_QUEUE";
    public const string FileName = "ocr-trace.jsonl";
    public const int DefaultMaxCharsPerPage = 8192;
    public const int DefaultQueueCapacity = 32;

    private static readonly AsyncLocal<OcrTraceContext?> CurrentContext = new();
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly ILogger _logger;
    private readonly Channel<OcrTraceItem> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private readonly int _maxChars;
    private long _dropped;
    private long _writeFailures;
    private long _lastDropWarnTicks;
    private int _disposed;
    private Task? _worker;

    private OcrTraceLog(ILogger logger, bool enabled, string path, int maxChars, int queueCapacity)
    {
        _logger = logger;
        Enabled = enabled;
        Path = path;
        _maxChars = maxChars;
        QueueCapacity = queueCapacity;
        // Wait mode: WriteAsync would block, TryWrite returns false as soon as the queue is full.
        // The request path must keep calling TryWrite and never WriteAsync.
        _channel = Channel.CreateBounded<OcrTraceItem>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool Enabled { get; }
    public string Path { get; }
    public int MaxCharsPerPage => _maxChars;
    public int QueueCapacity { get; }
    public long Dropped => Volatile.Read(ref _dropped);
    public long WriteFailures => Volatile.Read(ref _writeFailures);

    public static OcrTraceContext? Current => CurrentContext.Value;

    public static OcrTraceLog Create(ILogger logger, Func<string, string?>? env = null, string? directory = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        bool enabled = ParseBool(env(EnabledEnv), fallback: true);
        string? pathEnv = env(PathEnv);
        string path;
        if (!string.IsNullOrWhiteSpace(pathEnv))
            path = System.IO.Path.GetFullPath(pathEnv.Trim());
        else
        {
            string dir = string.IsNullOrWhiteSpace(directory)
                ? Directory.GetCurrentDirectory()
                : directory.Trim();
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, FileName));
        }

        int maxChars = DefaultMaxCharsPerPage;
        if (int.TryParse(env(MaxCharsEnv), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedChars))
            maxChars = Math.Clamp(parsedChars, 0, 1_000_000);

        int queue = DefaultQueueCapacity;
        if (int.TryParse(env(QueueEnv), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQueue))
            queue = Math.Clamp(parsedQueue, 1, 4096);

        return new OcrTraceLog(logger, enabled, path, maxChars, queue);
    }

    public static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Flows <paramref name="requestId"/> into cluster job start on this async chain
    /// so a later <see cref="ClusterJob"/> line can be joined to the HTTP request.
    /// </summary>
    public static RequestScope BeginScope(string requestId, string entry, string? fileId = null, int? teamId = null)
    {
        OcrTraceContext? prior = CurrentContext.Value;
        CurrentContext.Value = new OcrTraceContext(requestId, entry, fileId, teamId);
        return new RequestScope(prior);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _worker is not null)
            return Task.CompletedTask;
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation(
            "OCR trace on: path={Path} maxCharsPerPage={MaxChars} queue={Queue} (full queue drops events; OCR is not blocked)",
            Path,
            _maxChars,
            QueueCapacity);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_worker is null)
            return;
        try
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch
            {
                // Shutdown already gave up. Leave the unread tail on the floor.
            }
        }
    }

    /// <summary>Record a file URL exactly as received, including query and userinfo.</summary>
    public void Url(
        string entry,
        string requestId,
        string? url,
        string? fileId = null,
        int? teamId = null,
        string? jobId = null,
        string? nodeId = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            TryEnqueue(new OcrTraceUrlItem
            {
                Entry = entry,
                RequestId = requestId ?? "",
                FileId = NullIfEmpty(fileId),
                TeamId = teamId,
                JobId = NullIfEmpty(jobId),
                NodeId = NullIfEmpty(nodeId),
                Url = url,
            });
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Link a cluster job id to the HTTP request scope, if one is flowing.</summary>
    public void ClusterJob(string jobId, string? sourceUrl, int pageCount)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(jobId))
            return;
        try
        {
            OcrTraceContext? ctx = CurrentContext.Value;
            TryEnqueue(new OcrTraceJobItem
            {
                Entry = "cluster",
                RequestId = ctx?.RequestId ?? "",
                FileId = ctx?.FileId,
                TeamId = ctx?.TeamId,
                JobId = jobId,
                Url = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl,
                PageCount = pageCount,
            });
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Queue OCR text for one file. <paramref name="pages"/> is retained by reference until the
    /// background writer finishes; character data is not copied on this thread.
    /// </summary>
    public void Text(
        string entry,
        string requestId,
        string? fileId,
        string? url,
        int pageCount,
        OcrTracePage[] pages,
        int? teamId = null,
        string? downloadMode = null,
        string? path = null,
        string? fileName = null)
    {
        if (!Enabled)
            return;
        try
        {
            TryEnqueue(new OcrTraceTextItem
            {
                Entry = entry,
                RequestId = requestId ?? "",
                FileId = NullIfEmpty(fileId),
                TeamId = teamId,
                Url = string.IsNullOrWhiteSpace(url) ? null : url,
                Path = Clip(path, 1024),
                FileName = Clip(fileName, 256),
                DownloadMode = NullIfEmpty(downloadMode),
                PageCount = pageCount,
                Pages = pages ?? [],
            });
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Wait until items queued so far have been written. For tests and shutdown checks.</summary>
    public async Task<bool> DrainAsync(TimeSpan? timeout = null)
    {
        if (!Enabled)
            return true;
        if (_worker is null)
            return false;
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        using CancellationTokenSource cts = new(limit);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (_channel.Writer.TryWrite(new OcrTraceFlush(done)))
                    return await done.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                await Task.Delay(1, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        FileStream? stream = TryOpen();
        try
        {
            await foreach (OcrTraceItem item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item is OcrTraceFlush flush)
                {
                    try
                    {
                        if (stream is not null)
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The caller of DrainAsync still needs to wake up.
                    }

                    flush.Done.TrySetResult(true);
                    continue;
                }

                if (stream is null)
                {
                    Interlocked.Increment(ref _dropped);
                    MaybeAnnounceDrops();
                    continue;
                }

                try
                {
                    switch (item)
                    {
                        case OcrTraceUrlItem url:
                            await WriteUrlAsync(stream, url, ct).ConfigureAwait(false);
                            break;
                        case OcrTraceJobItem job:
                            await WriteJobAsync(stream, job, ct).ConfigureAwait(false);
                            break;
                        case OcrTraceTextItem text:
                            await WriteTextAsync(stream, text, ct).ConfigureAwait(false);
                            break;
                    }

                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _writeFailures);
                    MaybeAnnounceWriteFailure(ex);
                }

                MaybeAnnounceDrops();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                stream?.Dispose();
            }
            catch
            {
                // Closing the log must not surface on shutdown.
            }
        }
    }

    private FileStream? TryOpen()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new FileStream(
                Path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR trace file could not be opened at {Path}; events will be dropped", Path);
            return null;
        }
    }

    private ValueTask WriteUrlAsync(FileStream stream, OcrTraceUrlItem item, CancellationToken ct) =>
        WriteObjectAsync(stream, w =>
        {
            WriteHead(w, item.At, "url", item.Entry, item.RequestId, item.TeamId, item.FileId);
            WriteOpt(w, "jobId", item.JobId);
            WriteOpt(w, "nodeId", item.NodeId);
            w.WriteString("url", item.Url);
        }, ct);

    private ValueTask WriteJobAsync(FileStream stream, OcrTraceJobItem item, CancellationToken ct) =>
        WriteObjectAsync(stream, w =>
        {
            WriteHead(w, item.At, "job", item.Entry, item.RequestId, item.TeamId, item.FileId);
            w.WriteString("jobId", item.JobId);
            w.WriteNumber("pageCount", item.PageCount);
            if (!string.IsNullOrEmpty(item.Url))
                w.WriteString("url", item.Url);
        }, ct);

    private async Task WriteTextAsync(FileStream stream, OcrTraceTextItem item, CancellationToken ct)
    {
        long chars = 0;
        OcrTracePage[] pages = item.Pages;
        for (int i = 0; i < pages.Length; i++)
            chars += pages[i].Text?.Length ?? 0;

        await WriteObjectAsync(stream, w =>
        {
            WriteHead(w, item.At, "ocr", item.Entry, item.RequestId, item.TeamId, item.FileId);
            if (!string.IsNullOrEmpty(item.Url))
                w.WriteString("url", item.Url);
            WriteOpt(w, "path", item.Path);
            WriteOpt(w, "fileName", item.FileName);
            WriteOpt(w, "downloadMode", item.DownloadMode);
            w.WriteNumber("pageCount", item.PageCount);
            w.WriteNumber("loggedPages", pages.Length);
            w.WriteNumber("chars", chars);
            w.WriteNumber("maxChars", _maxChars);
        }, ct).ConfigureAwait(false);

        for (int i = 0; i < pages.Length; i++)
            await WritePageAsync(stream, item, pages[i], ct).ConfigureAwait(false);
    }

    private async Task WritePageAsync(FileStream stream, OcrTraceTextItem item, OcrTracePage page, CancellationToken ct)
    {
        FillPage(item, page);
        await stream.WriteAsync(_buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    private void FillPage(OcrTraceTextItem item, OcrTracePage page)
    {
        _buffer.Clear();
        using var writer = new Utf8JsonWriter(_buffer, WriterOptions);
        writer.WriteStartObject();
        WriteHead(writer, item.At, "page", item.Entry, item.RequestId, item.TeamId, item.FileId);
        writer.WriteNumber("page", page.Page);
        writer.WriteString("source", string.IsNullOrEmpty(page.Source) ? "ocr" : page.Source);
        string text = page.Text ?? "";
        int count = text.Length;
        bool omitBody = _maxChars == 0;
        bool truncated = omitBody ? count > 0 : count > _maxChars;
        writer.WriteNumber("chars", count);
        writer.WriteBoolean("truncated", truncated);
        if (!omitBody)
        {
            ReadOnlySpan<char> slice = truncated ? text.AsSpan(0, _maxChars) : text.AsSpan();
            writer.WriteString("text", slice);
        }

        writer.WriteEndObject();
        writer.Flush();
        _buffer.Write(NewLine);
    }

    private void WriteHead(
        Utf8JsonWriter w,
        DateTimeOffset at,
        string kind,
        string entry,
        string requestId,
        int? teamId,
        string? fileId)
    {
        w.WriteString("ts", at.ToString("O", CultureInfo.InvariantCulture));
        w.WriteString("kind", kind);
        w.WriteString("entry", entry);
        if (!string.IsNullOrEmpty(requestId))
            w.WriteString("requestId", requestId);
        if (teamId is int team)
            w.WriteNumber("teamId", team);
        WriteOpt(w, "fileId", fileId);
    }

    private static void WriteOpt(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            w.WriteString(name, value);
    }

    private async ValueTask WriteObjectAsync(FileStream stream, Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        _buffer.Clear();
        using (var writer = new Utf8JsonWriter(_buffer, WriterOptions))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        // One buffer per line. The write itself is async so a slow disk does not pin a worker thread.
        _buffer.Write(NewLine);
        await stream.WriteAsync(_buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    private void TryEnqueue(OcrTraceItem item)
    {
        if (!_channel.Writer.TryWrite(item))
            Interlocked.Increment(ref _dropped);
    }

    private void MaybeAnnounceDrops()
    {
        long dropped = Volatile.Read(ref _dropped);
        if (dropped <= 0)
            return;
        long now = Environment.TickCount64;
        long last = Volatile.Read(ref _lastDropWarnTicks);
        if (last != 0 && now - last < 30_000)
            return;
        if (Interlocked.CompareExchange(ref _lastDropWarnTicks, now, last) != last)
            return;
        _logger.LogWarning(
            "OCR trace dropped {Dropped} events (queue full or log file unavailable). OCR results were not affected.",
            dropped);
    }

    private void MaybeAnnounceWriteFailure(Exception ex)
    {
        long now = Environment.TickCount64;
        long last = Volatile.Read(ref _lastDropWarnTicks);
        if (last != 0 && now - last < 30_000)
            return;
        if (Interlocked.CompareExchange(ref _lastDropWarnTicks, now, last) != last)
            return;
        _logger.LogWarning(ex, "OCR trace write failed at {Path}. OCR results were not affected.", Path);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static bool ParseBool(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        string s = raw.Trim();
        if (bool.TryParse(s, out bool b))
            return b;
        if (s is "1" or "yes" or "YES" or "on" or "ON")
            return true;
        if (s is "0" or "no" or "NO" or "off" or "OFF")
            return false;
        return fallback;
    }

    /// <summary>Restores the previous request scope. Dispose on the OCR call's async chain.</summary>
    public sealed class RequestScope : IDisposable
    {
        private OcrTraceContext? _prior;
        private bool _active;

        internal RequestScope(OcrTraceContext? prior)
        {
            _prior = prior;
            _active = true;
        }

        public void Dispose()
        {
            if (!_active)
                return;
            _active = false;
            CurrentContext.Value = _prior;
            _prior = null;
        }
    }

    public sealed class OcrTraceContext
    {
        public OcrTraceContext(string requestId, string entry, string? fileId, int? teamId)
        {
            RequestId = requestId;
            Entry = entry;
            FileId = fileId;
            TeamId = teamId;
        }

        public string RequestId { get; }
        public string Entry { get; }
        public string? FileId { get; }
        public int? TeamId { get; }
    }
}

internal abstract class OcrTraceItem
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class OcrTraceUrlItem : OcrTraceItem
{
    public string Entry { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string? FileId { get; init; }
    public int? TeamId { get; init; }
    public string? JobId { get; init; }
    public string? NodeId { get; init; }
    public string Url { get; init; } = "";
}

internal sealed class OcrTraceJobItem : OcrTraceItem
{
    public string Entry { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string? FileId { get; init; }
    public int? TeamId { get; init; }
    public string JobId { get; init; } = "";
    public string? Url { get; init; }
    public int PageCount { get; init; }
}

internal sealed class OcrTraceTextItem : OcrTraceItem
{
    public string Entry { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string? FileId { get; init; }
    public int? TeamId { get; init; }
    public string? Url { get; init; }
    public string? Path { get; init; }
    public string? FileName { get; init; }
    public string? DownloadMode { get; init; }
    public int PageCount { get; init; }
    public OcrTracePage[] Pages { get; init; } = [];
}

internal sealed class OcrTraceFlush : OcrTraceItem
{
    public OcrTraceFlush(TaskCompletionSource<bool> done) => Done = done;
    public TaskCompletionSource<bool> Done { get; }
}
