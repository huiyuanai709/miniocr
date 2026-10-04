using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MiniOcr.Services;

// Scaled stand-in for the 463-page run: one throttled PDF link, a coordinator, and two
// workers. One worker sleeps longer per page. "legacy" joins only after a single-stream
// download and leases fixed 16-page batches. "new" joins first, fetches the source with
// ranged ParallelPdfDownloader, and uses the real adaptive scheduler plus NER-blocker copy.
if (Arg(args, "--role") == "worker")
    return await WorkerAsync(args);
if (Arg(args, "--role") == "coordinator")
    return await CoordinatorAsync(args);
return await DriveAsync();

static async Task<int> DriveAsync()
{
    int pdfPort = FreePort();
    byte[] pdf = new byte[2 * 1024 * 1024];
    Random.Shared.NextBytes(pdf);
    using var source = new ThrottledPdfServer(pdfPort, pdf, bytesPerSecond: 2 * 1024 * 1024);
    source.Start();
    string sourceUrl = "http://127.0.0.1:" + pdfPort + "/doc";

    BenchReport legacy = await RunScenarioAsync("legacy", sourceUrl);
    BenchReport current = await RunScenarioAsync("new", sourceUrl);
    Print(legacy, current);

    int failed = 0;
    void Check(bool ok, string msg)
    {
        Console.WriteLine(ok ? "  PASS  " + msg : "  FAIL  " + msg);
        if (!ok)
            failed++;
    }

    Check(current.JoinFastMs >= 0 && legacy.JoinFastMs > current.JoinFastMs + 300,
        "new workers join before the legacy download-then-join");
    Check(current.ReadyFastMs >= 0 && legacy.ReadyFastMs > current.ReadyFastMs + 200,
        "ranged source fetch is faster than one throttled stream");
    Check(current.WallMs > 0 && current.WallMs < legacy.WallMs, "new wall time is lower");
    Check(current.FirstBatchSlow is > 0 and <= 2 && legacy.FirstBatchSlow >= 8,
        "slow node starts with a small batch");
    if (failed > 0)
    {
        Console.WriteLine($"FAILED {failed}");
        return 1;
    }

    Console.WriteLine("ALL PASSED");
    return 0;
}

static async Task<BenchReport> RunScenarioAsync(string mode, string sourceUrl)
{
    int port = FreePort();
    string coord = "http://127.0.0.1:" + port;
    var coordErr = new StringBuilder();
    var fastErr = new StringBuilder();
    var slowErr = new StringBuilder();
    using Process coordinator = StartSelf(
        "--role coordinator --listen " + port + " --source " + sourceUrl + " --mode " + mode,
        drainOutput: false,
        coordErr);
    using Process fast = StartSelf("--role worker --id fast --coord " + coord + " --delay 20", drainOutput: true, fastErr);
    using Process slow = StartSelf("--role worker --id slow --coord " + coord + " --delay 500", drainOutput: true, slowErr);
    try
    {
        await WaitHealthAsync(coord + "/health", TimeSpan.FromSeconds(15));
        await Task.Delay(200);
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        using HttpResponseMessage start = await http.PostAsync(coord + "/start", new StringContent(""));
        start.EnsureSuccessStatusCode();
        string? line = await ReadBenchLineAsync(coordinator, TimeSpan.FromSeconds(40));
        if (line is null)
        {
            throw new InvalidOperationException(
                mode + " produced no BENCH line.\ncoord: " + coordErr + "\nfast: " + fastErr + "\nslow: " + slowErr);
        }

        return BenchReport.Parse(line);
    }
    finally
    {
        Kill(coordinator);
        Kill(fast);
        Kill(slow);
    }
}

static void Print(BenchReport legacy, BenchReport current)
{
    Console.WriteLine();
    Console.WriteLine("mode     wallMs  joinFast  joinSlow  readyFast  readySlow  pages c/f/s  firstSlow  maxSlow  group10  group20");
    PrintRow(legacy);
    PrintRow(current);
    Console.WriteLine();
    Console.WriteLine(
        $"delta wall {legacy.WallMs - current.WallMs} ms, joinFast {legacy.JoinFastMs - current.JoinFastMs} ms, readyFast {legacy.ReadyFastMs - current.ReadyFastMs} ms, group10 {legacy.Group10Ms - current.Group10Ms} ms");
}

static void PrintRow(BenchReport r) =>
    Console.WriteLine(
        $"{r.Mode,-8} {r.WallMs,6} {r.JoinFastMs,9} {r.JoinSlowMs,9} {r.ReadyFastMs,10} {r.ReadySlowMs,10} {r.PagesCoord,5}/{r.PagesFast}/{r.PagesSlow,-3} {r.FirstBatchSlow,10} {r.MaxBatchSlow,8} {r.Group10Ms,8} {r.Group20Ms,8}");

static async Task<int> CoordinatorAsync(string[] args)
{
    int port = int.Parse(Arg(args, "--listen") ?? "0");
    string sourceUrl = Arg(args, "--source") ?? "";
    string mode = Arg(args, "--mode") ?? "new";
    bool adaptive = mode == "new";
    const int pages = 40;
    byte[] body = new byte[2 * 1024 * 1024];
    var sched = new ClusterPageScheduler(new ClusterScheduleOptions
    {
        PageCount = pages,
        LocalNodeId = "coord",
        PagesPerBatch = adaptive ? 0 : 16,
        LeaseFloorMs = 30_000,
        PageTimeoutMs = 5_000,
        LeaseCapMs = 60_000,
        SpeculativeTailPages = 4,
        ExpectedNodes = 3,
    });
    sched.SetCapacity("coord", 8);
    sched.SetCapacity("fast", 8);
    sched.SetCapacity("slow", 16);
    if (adaptive)
        sched.SetHoldLocalWindow(true);

    var job = new JobState(sched, pages, adaptive, sourceUrl);
    using var listener = new HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
    listener.Start();
    using var lifetime = new CancellationTokenSource();
    Task accept = AcceptLoopAsync(listener, job, body, lifetime.Token);
    await job.Started.Task.ConfigureAwait(false);
    Task local = LocalOcrAsync(job);
    await job.Finished.Task.ConfigureAwait(false);
    lifetime.Cancel();
    try { listener.Stop(); } catch { }
    await Quiet(local);
    await Quiet(accept);
    BenchReport report = job.Report(mode);
    Console.WriteLine("BENCH " + report.ToJson());
    return 0;
}

static async Task LocalOcrAsync(JobState job)
{
    try
    {
        while (!job.Sched.IsComplete)
        {
            if (job.Adaptive)
                job.Sched.SetNerBlocker(job.EarliestMissing());
            ClusterClaim claim = job.Sched.Claim("coord", 8, DateTimeOffset.UtcNow);
            if (claim.Kind == ClusterClaimKind.Done)
                break;
            if (claim.Kind != ClusterClaimKind.Batch)
            {
                await job.Sched.WaitForChangeAsync(claim.Version, TimeSpan.FromMilliseconds(100), CancellationToken.None)
                    .ConfigureAwait(false);
                continue;
            }

            job.NoteBatch("coord", claim.Pages.Length);
            await Task.Delay(claim.Pages.Length * 80).ConfigureAwait(false);
            foreach (int page in claim.Pages)
                job.Commit("coord", claim.BatchId, page);
        }
    }
    finally
    {
        job.CancelReads();
        job.Finished.TrySetResult();
    }
}

static async Task AcceptLoopAsync(HttpListener listener, JobState job, byte[] pdf, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            break;
        }

        _ = Task.Run(() => HandleAsync(ctx, job, pdf), CancellationToken.None);
    }
}

static async Task HandleAsync(HttpListenerContext ctx, JobState job, byte[] pdf)
{
    try
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path == "/health")
        {
            await WriteTextAsync(ctx, 200, "ok");
            return;
        }

        if (path == "/start")
        {
            job.Start();
            await WriteTextAsync(ctx, 200, "ok");
            return;
        }

        if (path == "/dispatch")
        {
            string json = job.Started.Task.IsCompleted
                ? "{\"wait\":false,\"sourceUrl\":" + JsonQuoted(job.SourceUrl) + "}"
                : "{\"wait\":true}";
            await WriteTextAsync(ctx, 200, json);
            return;
        }

        if (path == "/join")
        {
            using JsonDocument doc = await ReadJsonAsync(ctx.Request);
            string node = doc.RootElement.GetProperty("node").GetString() ?? "";
            bool downloading = doc.RootElement.TryGetProperty("downloading", out JsonElement d) && d.GetBoolean();
            job.Join(node, downloading);
            await WriteTextAsync(ctx, 200, "{\"ok\":true}");
            return;
        }

        if (path == "/claim")
        {
            if (!job.Started.Task.IsCompleted)
            {
                await WriteTextAsync(ctx, 200, "{\"wait\":true}");
                return;
            }

            using JsonDocument doc = await ReadJsonAsync(ctx.Request);
            string node = doc.RootElement.GetProperty("node").GetString() ?? "";
            if (job.Adaptive)
                job.Sched.SetNerBlocker(job.EarliestMissing());
            ClusterClaim claim = job.Sched.Claim(node, 16, DateTimeOffset.UtcNow);
            if (claim.Kind == ClusterClaimKind.Done)
            {
                await WriteTextAsync(ctx, 200, "{\"done\":true}");
                return;
            }

            if (claim.Kind != ClusterClaimKind.Batch)
            {
                await WriteTextAsync(ctx, 200, "{\"wait\":true}");
                return;
            }

            job.NoteBatch(node, claim.Pages.Length);
            await WriteTextAsync(ctx, 200, "{\"batchId\":" + JsonQuoted(claim.BatchId) + ",\"pages\":[" + string.Join(',', claim.Pages) + "]}");
            return;
        }

        if (path == "/result")
        {
            using JsonDocument doc = await ReadJsonAsync(ctx.Request);
            string node = doc.RootElement.GetProperty("node").GetString() ?? "";
            string batch = doc.RootElement.GetProperty("batchId").GetString() ?? "";
            foreach (JsonElement page in doc.RootElement.GetProperty("pages").EnumerateArray())
                job.Commit(node, batch, page.GetInt32());
            await WriteTextAsync(ctx, 200, "{\"ok\":true}");
            return;
        }

        if (path == "/pdf")
        {
            await ServePdfAsync(ctx, pdf, job.ReadToken);
            return;
        }

        await WriteTextAsync(ctx, 404, "no");
    }
    catch
    {
        try { ctx.Response.Abort(); } catch { }
    }
}

static async Task ServePdfAsync(HttpListenerContext ctx, byte[] pdf, CancellationToken ct)
{
    int start = 0;
    int end = pdf.Length - 1;
    bool partial = false;
    string? range = ctx.Request.Headers["Range"];
    if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.Ordinal) && !range.Contains(','))
    {
        string spec = range["bytes=".Length..];
        string[] parts = spec.Split('-', 2);
        if (parts.Length == 2
            && long.TryParse(string.IsNullOrEmpty(parts[0]) ? null : parts[0], out long fromOrZero)
            && (string.IsNullOrEmpty(parts[1]) || long.TryParse(parts[1], out _)))
        {
            long? from = string.IsNullOrEmpty(parts[0]) ? null : fromOrZero;
            long? to = string.IsNullOrEmpty(parts[1]) ? null : long.Parse(parts[1]);
            if (ClusterPdfRange.TrySlice(pdf.Length, from, to, out long s, out long e))
            {
                start = (int)s;
                end = (int)e;
                partial = true;
            }
        }
    }

    int count = end - start + 1;
    ctx.Response.StatusCode = partial ? 206 : 200;
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.ContentLength64 = count;
    ctx.Response.Headers["Accept-Ranges"] = "bytes";
    if (partial)
        ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{pdf.Length}";
    if (ctx.Request.HttpMethod == "HEAD")
    {
        ctx.Response.Close();
        return;
    }

    const int chunk = 16 * 1024;
    int offset = start;
    int left = count;
    long rate = 2 * 1024 * 1024;
    while (left > 0)
    {
        ct.ThrowIfCancellationRequested();
        int n = Math.Min(chunk, left);
        await ctx.Response.OutputStream.WriteAsync(pdf.AsMemory(offset, n), ct).ConfigureAwait(false);
        offset += n;
        left -= n;
        int delayUs = (int)(n * 1_000_000L / rate);
        if (delayUs > 0)
            await Task.Delay(TimeSpan.FromMicroseconds(delayUs), ct).ConfigureAwait(false);
    }

    ctx.Response.Close();
}

static async Task<int> WorkerAsync(string[] args)
{
    string id = Arg(args, "--id") ?? "worker";
    string coord = (Arg(args, "--coord") ?? "").TrimEnd('/');
    int delay = int.Parse(Arg(args, "--delay") ?? "20");
    using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    string? source = null;
    for (int i = 0; i < 400; i++)
    {
        using HttpResponseMessage resp = await http.PostAsync(coord + "/dispatch", new StringContent("{}"));
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("wait", out JsonElement wait) && wait.GetBoolean())
        {
            await Task.Delay(50);
            continue;
        }

        source = doc.RootElement.GetProperty("sourceUrl").GetString();
        break;
    }

    if (string.IsNullOrWhiteSpace(source))
        return 2;

    bool legacy = source.Contains("mode=legacy", StringComparison.Ordinal) || Arg(args, "--legacy") == "1";
    // The coordinator tells the mode by appending a query the worker also receives via --mode on the source URL.
    // Drive passes the real source; the mode is a sibling flag forwarded by the coordinator in the source URL query.
    bool oldPath = source.Contains("bench=legacy", StringComparison.Ordinal);
    if (oldPath || legacy)
    {
        await DownloadSingleAsync(http, coord + "/pdf");
        await PostJsonAsync(http, coord + "/join", "{\"node\":" + JsonQuoted(id) + ",\"downloading\":false}");
    }
    else
    {
        await PostJsonAsync(http, coord + "/join", "{\"node\":" + JsonQuoted(id) + ",\"downloading\":true}");
        await DownloadRangedAsync(source);
        await PostJsonAsync(http, coord + "/join", "{\"node\":" + JsonQuoted(id) + ",\"downloading\":false}");
    }

    while (true)
    {
        using HttpResponseMessage resp = await http.PostAsync(
            coord + "/claim",
            new StringContent("{\"node\":" + JsonQuoted(id) + "}", Encoding.UTF8, "application/json"));
        string text = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("done", out JsonElement done) && done.GetBoolean())
            return 0;
        if (!doc.RootElement.TryGetProperty("batchId", out JsonElement batchEl))
        {
            await Task.Delay(30);
            continue;
        }

        int[] pages = doc.RootElement.GetProperty("pages").EnumerateArray().Select(p => p.GetInt32()).ToArray();
        await Task.Delay(Math.Max(1, pages.Length) * delay);
        string body = "{\"node\":" + JsonQuoted(id) + ",\"batchId\":" + JsonQuoted(batchEl.GetString() ?? "") +
                      ",\"pages\":[" + string.Join(',', pages) + "]}";
        await PostJsonAsync(http, coord + "/result", body);
    }
}

static async Task DownloadSingleAsync(HttpClient http, string url)
{
    using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();
    await using Stream stream = await resp.Content.ReadAsStreamAsync();
    byte[] buf = new byte[16 * 1024];
    while (await stream.ReadAsync(buf) > 0)
    {
    }
}

static async Task DownloadRangedAsync(string url)
{
    using ILoggerFactory logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
    using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
    var downloader = new ParallelPdfDownloader(http, logs.CreateLogger<ParallelPdfDownloader>());
    ParallelPdfDownloader.DownloadResult result = await downloader.DownloadAsync(url, CancellationToken.None);
    using RentedBuffer buffer = result.Buffer;
    if (buffer.Length <= 0)
        throw new InvalidOperationException("empty pdf");
}

static async Task PostJsonAsync(HttpClient http, string url, string json)
{
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    using HttpResponseMessage resp = await http.PostAsync(url, content);
    resp.EnsureSuccessStatusCode();
}

static Process StartSelf(string arguments, bool drainOutput, StringBuilder errors)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
    {
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    Process? process = Process.Start(psi);
    if (process is null)
        throw new InvalidOperationException("failed to start " + arguments);
    process.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is not null)
            errors.AppendLine(e.Data);
    };
    process.BeginErrorReadLine();
    if (drainOutput)
        process.BeginOutputReadLine();
    return process;
}

static async Task WaitHealthAsync(string url, TimeSpan timeout)
{
    using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
                return;
        }
        catch
        {
        }

        await Task.Delay(50);
    }

    throw new TimeoutException("health " + url);
}

static async Task<string?> ReadBenchLineAsync(Process process, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        string? line = await process.StandardOutput.ReadLineAsync();
        if (line is null)
            return null;
        if (line.StartsWith("BENCH ", StringComparison.Ordinal))
            return line;
    }

    return null;
}

static void Kill(Process process)
{
    try
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }
    catch
    {
    }
}

static async Task Quiet(Task task)
{
    try { await task.ConfigureAwait(false); } catch { }
}

static async Task<JsonDocument> ReadJsonAsync(HttpListenerRequest request)
{
    using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
    string text = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(text))
        text = "{}";
    return JsonDocument.Parse(text);
}

static async Task WriteTextAsync(HttpListenerContext ctx, int status, string text)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}

static string JsonQuoted(string? value) => JsonSerializer.Serialize(value ?? "");

static string? Arg(string[] args, string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == name && i + 1 < args.Length)
            return args[i + 1];
        if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            return args[i][(name.Length + 1)..];
    }

    return null;
}

static int FreePort()
{
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

sealed class JobState
{
    private readonly bool[] _have;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _maxBatch = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _firstBatch = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _reads = new();
    private long _t0;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ClusterPageScheduler Sched { get; }
    public bool Adaptive { get; }
    public string SourceUrl { get; }
    public long JoinFastMs { get; private set; } = -1;
    public long JoinSlowMs { get; private set; } = -1;
    public long ReadyFastMs { get; private set; } = -1;
    public long ReadySlowMs { get; private set; } = -1;
    public long Group10Ms { get; private set; } = -1;
    public long Group20Ms { get; private set; } = -1;
    public CancellationToken ReadToken => _reads.Token;

    public JobState(ClusterPageScheduler sched, int pages, bool adaptive, string sourceUrl)
    {
        Sched = sched;
        _have = new bool[pages];
        Adaptive = adaptive;
        SourceUrl = sourceUrl + (sourceUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
                    "bench=" + (adaptive ? "new" : "legacy");
    }

    public void Start()
    {
        lock (_gate)
        {
            if (Started.Task.IsCompleted)
                return;
            _t0 = Stopwatch.GetTimestamp();
        }

        Started.TrySetResult();
    }

    public void Join(string node, bool downloading)
    {
        long ms = Elapsed();
        bool hold = Adaptive && downloading;
        lock (_gate)
        {
            if (node == "fast" && JoinFastMs < 0)
                JoinFastMs = ms;
            if (node == "slow" && JoinSlowMs < 0)
                JoinSlowMs = ms;
            if (!downloading)
            {
                if (node == "fast")
                    ReadyFastMs = ms;
                if (node == "slow")
                    ReadySlowMs = ms;
            }
        }

        if (Adaptive)
            Sched.SetHoldLocalWindow(hold);
    }

    public void NoteBatch(string node, int count)
    {
        lock (_gate)
        {
            if (!_firstBatch.ContainsKey(node))
                _firstBatch[node] = count;
            _maxBatch[node] = Math.Max(_maxBatch.GetValueOrDefault(node), count);
        }
    }

    public void Commit(string node, string batch, int page)
    {
        if (!Sched.TryCommit(batch, page))
            return;
        lock (_gate)
        {
            _pages[node] = _pages.GetValueOrDefault(node) + 1;
            if ((uint)(page - 1) < (uint)_have.Length)
                _have[page - 1] = true;
            int prefix = 0;
            while (prefix < _have.Length && _have[prefix])
                prefix++;
            long ms = Elapsed();
            if (prefix >= 10 && Group10Ms < 0)
                Group10Ms = ms;
            if (prefix >= 20 && Group20Ms < 0)
                Group20Ms = ms;
        }
    }

    public int? EarliestMissing()
    {
        lock (_gate)
        {
            for (int i = 0; i < _have.Length; i++)
            {
                if (!_have[i])
                    return i + 1;
            }

            return null;
        }
    }

    public void CancelReads() => _reads.Cancel();

    public BenchReport Report(string mode)
    {
        long wall = Elapsed();
        lock (_gate)
        {
            return new BenchReport(
                mode,
                wall,
                JoinFastMs,
                JoinSlowMs,
                ReadyFastMs,
                ReadySlowMs,
                _pages.GetValueOrDefault("coord"),
                _pages.GetValueOrDefault("fast"),
                _pages.GetValueOrDefault("slow"),
                _firstBatch.GetValueOrDefault("slow"),
                _maxBatch.GetValueOrDefault("slow"),
                Group10Ms,
                Group20Ms);
        }
    }

    private long Elapsed()
    {
        long start;
        lock (_gate)
            start = _t0;
        if (start == 0)
            return 0;
        return (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}

sealed record BenchReport(
    string Mode,
    long WallMs,
    long JoinFastMs,
    long JoinSlowMs,
    long ReadyFastMs,
    long ReadySlowMs,
    int PagesCoord,
    int PagesFast,
    int PagesSlow,
    int FirstBatchSlow,
    int MaxBatchSlow,
    long Group10Ms,
    long Group20Ms)
{
    public string ToJson() =>
        JsonSerializer.Serialize(this);

    public static BenchReport Parse(string line)
    {
        const string prefix = "BENCH ";
        string json = line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line;
        return JsonSerializer.Deserialize<BenchReport>(json)
            ?? throw new InvalidOperationException("bad bench line " + line);
    }
}

sealed class ThrottledPdfServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _pdf;
    private readonly long _rate;
    private readonly CancellationTokenSource _cts = new();

    public ThrottledPdfServer(int port, byte[] pdf, long bytesPerSecond)
    {
        _pdf = pdf;
        _rate = bytesPerSecond;
        _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(ctx));
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx)
    {
        try
        {
            int start = 0;
            int end = _pdf.Length - 1;
            bool partial = false;
            string? range = ctx.Request.Headers["Range"];
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                string spec = range["bytes=".Length..].Split(',')[0];
                string[] parts = spec.Split('-', 2);
                long? from = string.IsNullOrEmpty(parts[0]) ? null : long.Parse(parts[0]);
                long? to = parts.Length < 2 || string.IsNullOrEmpty(parts[1]) ? null : long.Parse(parts[1]);
                if (ClusterPdfRange.TrySlice(_pdf.Length, from, to, out long s, out long e))
                {
                    start = (int)s;
                    end = (int)e;
                    partial = true;
                }
            }

            int count = end - start + 1;
            ctx.Response.StatusCode = partial ? 206 : 200;
            ctx.Response.ContentType = "application/pdf";
            ctx.Response.ContentLength64 = count;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            if (partial)
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{_pdf.Length}";
            if (ctx.Request.HttpMethod == "HEAD")
            {
                ctx.Response.Close();
                return;
            }

            const int chunk = 32 * 1024;
            int offset = start;
            int left = count;
            while (left > 0)
            {
                int n = Math.Min(chunk, left);
                await ctx.Response.OutputStream.WriteAsync(_pdf.AsMemory(offset, n));
                offset += n;
                left -= n;
                await Task.Delay(TimeSpan.FromMicroseconds(n * 1_000_000L / _rate));
            }

            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
        _cts.Dispose();
    }
}
