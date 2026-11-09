using System.Net;
using Microsoft.Extensions.Logging;
using MiniOcr.Services;
using PDFtoImage;
using SkiaSharp;

// Proves URL fetch follows redirects and sends Range to the final URL, and that a
// server without Range falls back to a full download.

string samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "sample-multipage.pdf"));
if (!File.Exists(samplePath))
    samplePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "samples", "sample-multipage.pdf"));
if (!File.Exists(samplePath))
    samplePath = "/workspace/samples/sample-multipage.pdf";
byte[] pdf = File.ReadAllBytes(samplePath);

int pageCount;
using (var count = new MemoryStream(pdf, writable: false))
    pageCount = Conversion.GetPageCount(count, leaveOpen: false);
if (pageCount <= 0)
    throw new InvalidOperationException("sample PDF has no pages");

using ILoggerFactory logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
ILogger<ParallelPdfDownloader> logger = logs.CreateLogger<ParallelPdfDownloader>();

await TestRangeFollowsRedirect(pdf, pageCount, logger);
await TestFullFallbackWhenRangeMissing(pdf, logger);
await TestContiguousSpan();

Console.WriteLine("pdf-range-smoke ok");

static async Task TestRangeFollowsRedirect(byte[] pdf, int expectedPages, ILogger<ParallelPdfDownloader> logger)
{
    using var server = PdfServer.Start(pdf, supportRange: true, redirect: true);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var downloader = new ParallelPdfDownloader(http, logger);

    using PdfRemoteDocument partial = await downloader.AcquireAsync(server.StartUrl, partialOk: true, CancellationToken.None);
    if (!partial.IsPartial || partial.Session is null)
        throw new InvalidOperationException("expected an HTTP Range session, mode=" + partial.Mode);
    if (partial.Mode != "http-range")
        throw new InvalidOperationException("mode " + partial.Mode);
    if (partial.Session.PageCount != expectedPages)
        throw new InvalidOperationException($"pages {partial.Session.PageCount} != {expectedPages}");
    if (partial.BytesFetched >= partial.Length)
        throw new InvalidOperationException($"range open fetched the whole file ({partial.BytesFetched}/{partial.Length})");
    if (!partial.FinalUri.AbsoluteUri.Contains("sig=changed", StringComparison.Ordinal))
        throw new InvalidOperationException("final URL lost the signed query: " + partial.FinalUri);

    using SKBitmap page = partial.Session.Render(0, new RenderOptions(Dpi: 36));
    if (page.Width <= 0)
        throw new InvalidOperationException("rendered page is empty");

    List<Hit> ranged = server.Hits.Where(hit => hit.Range is not null).ToList();
    if (ranged.Count == 0)
        throw new InvalidOperationException("no Range requests were sent");
    foreach (Hit hit in ranged)
    {
        if (!hit.PathAndQuery.StartsWith("/final.pdf?sig=changed", StringComparison.Ordinal))
            throw new InvalidOperationException("Range was not sent to the final URL: " + hit.PathAndQuery);
    }

    using PdfRemoteDocument full = await downloader.AcquireAsync(server.StartUrl, partialOk: false, CancellationToken.None);
    if (full.IsPartial || full.Buffer is null)
        throw new InvalidOperationException("full acquire should buffer the PDF, mode=" + full.Mode);
    if (full.Mode != "http-range-full")
        throw new InvalidOperationException("mode " + full.Mode);
    if (full.Buffer.Length != pdf.Length)
        throw new InvalidOperationException($"full buffer {full.Buffer.Length} != {pdf.Length}");
}

static async Task TestFullFallbackWhenRangeMissing(byte[] pdf, ILogger<ParallelPdfDownloader> logger)
{
    using var server = PdfServer.Start(pdf, supportRange: false, redirect: true);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var downloader = new ParallelPdfDownloader(http, logger);
    using PdfRemoteDocument doc = await downloader.AcquireAsync(server.StartUrl, partialOk: true, CancellationToken.None);
    if (doc.IsPartial || doc.Buffer is null)
        throw new InvalidOperationException("missing Range should full-download, mode=" + doc.Mode);
    if (doc.Mode != "http-full-fallback")
        throw new InvalidOperationException("mode " + doc.Mode);
    if (doc.Buffer.Length != pdf.Length)
        throw new InvalidOperationException($"fallback length {doc.Buffer.Length} != {pdf.Length}");
    if (!doc.Buffer.Span.SequenceEqual(pdf))
        throw new InvalidOperationException("fallback bytes do not match the PDF");
}

static Task TestContiguousSpan()
{
    if (!PdfPageSpan.TryContiguous([2, 3, 4], out int first, out int count) || first != 2 || count != 3)
        throw new InvalidOperationException("contiguous span was not detected");
    if (PdfPageSpan.TryContiguous([0, 2], out _, out _))
        throw new InvalidOperationException("sparse pages were treated as contiguous");
    return Task.CompletedTask;
}

file sealed class Hit
{
    public string PathAndQuery { get; init; } = "";
    public string? Range { get; init; }
}

file sealed class PdfServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _pdf;
    private readonly bool _supportRange;
    private readonly bool _redirect;
    private readonly List<Hit> _hits = [];
    private readonly Task _loop;

    private PdfServer(string prefix, byte[] pdf, bool supportRange, bool redirect)
    {
        _pdf = pdf;
        _supportRange = supportRange;
        _redirect = redirect;
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string StartUrl { get; private init; } = "";

    public IReadOnlyList<Hit> Hits
    {
        get
        {
            lock (_hits)
                return _hits.ToArray();
        }
    }

    public static PdfServer Start(byte[] pdf, bool supportRange, bool redirect)
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        string prefix = $"http://127.0.0.1:{port}/";
        var server = new PdfServer(prefix, pdf, supportRange, redirect)
        {
            StartUrl = prefix + "start",
        };
        return server;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* shutting down */ }
        try { _listener.Close(); } catch { /* shutting down */ }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            try
            {
                Handle(context);
            }
            catch
            {
                try { context.Response.Abort(); } catch { /* client went away */ }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        string path = context.Request.Url?.PathAndQuery ?? context.Request.RawUrl ?? "";
        string? range = context.Request.Headers["Range"];
        lock (_hits)
            _hits.Add(new Hit { PathAndQuery = path, Range = range });

        if (_redirect && path.StartsWith("/start", StringComparison.Ordinal))
        {
            string final = context.Request.Url!.GetLeftPart(UriPartial.Authority) + "/final.pdf?sig=changed";
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = final;
            context.Response.Close();
            return;
        }

        context.Response.AddHeader("Accept-Ranges", _supportRange ? "bytes" : "none");
        if (string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = _pdf.Length;
            context.Response.Close();
            return;
        }

        if (_supportRange && range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            string spec = range["bytes=".Length..].Trim();
            string[] parts = spec.Split('-');
            if (parts.Length == 2 && long.TryParse(parts[0], out long from))
            {
                long to = parts[1].Length == 0 ? _pdf.Length - 1 : long.Parse(parts[1]);
                if (from < 0)
                    from = 0;
                if (to >= _pdf.Length)
                    to = _pdf.Length - 1;
                int length = (int)(to - from + 1);
                context.Response.StatusCode = 206;
                context.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{_pdf.Length}");
                context.Response.ContentLength64 = length;
                context.Response.OutputStream.Write(_pdf, (int)from, length);
                context.Response.Close();
                return;
            }
        }

        context.Response.StatusCode = 200;
        context.Response.ContentLength64 = _pdf.Length;
        context.Response.OutputStream.Write(_pdf, 0, _pdf.Length);
        context.Response.Close();
    }
}
