using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MiniOcr.Models;
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
TestPageSpec(pageCount);
await TestRenderRequestedPages(pdf, pageCount, logger);

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

static void TestPageSpec(int documentPages)
{
    if (PdfPageSpec.Parse(null) is not null)
        throw new InvalidOperationException("omitted pages must mean the whole document");
    if (PdfPageSpec.Combine(null, null) is not null)
        throw new InvalidOperationException("missing body and query must mean the whole document");

    Expect("1-10", PdfPageSpec.Parse("1-10")!.Canonical, "first 10");
    Expect("5-12", PdfPageSpec.Parse(" 5 - 12 ")!.Canonical, "mid range");
    Expect("1,3,5-8", PdfPageSpec.Parse("1,3,5-8")!.Canonical, "list");
    Expect("1-5", PdfPageSpec.Parse("1-3,2-5")!.Canonical, "overlapping ranges merge");
    Expect("1-3,7-8", PdfPageSpec.Parse("8,7,7,1-3")!.Canonical, "pages are sorted and deduped");
    Expect("5-7,12", PdfPageSpec.FromPageNumbers([12, 5, 6, 7, 6]).Canonical, "array becomes the same list");

    PdfPageSpec? queryWins = PdfPageSpec.Combine(null, "5-12");
    if (queryWins?.Canonical != "5-12")
        throw new InvalidOperationException("query pages are used when the body omits them");
    if (PdfPageSpec.Combine("1-10", "5-12")?.Canonical != "1-10")
        throw new InvalidOperationException("body pages win over the query");

    PdfPageWindow first = PdfPageSpec.Parse("1-2")!.Resolve(documentPages);
    if (first.Clamped || first.Processed != "1-2" || !first.ZeroBased.SequenceEqual([0, 1]))
        throw new InvalidOperationException("1-2 did not resolve to a 0-based prefix");

    if (documentPages >= 3)
    {
        PdfPageWindow mid = PdfPageSpec.Parse("2-4")!.Resolve(documentPages);
        if (mid.Clamped || mid.Processed != "2-4" || !mid.ZeroBased.SequenceEqual([1, 2, 3]))
            throw new InvalidOperationException("mid-document range was not 0-based [1,2,3]");
    }

    PdfPageWindow clamped = PdfPageSpec.Parse("1-10")!.Resolve(3);
    if (!clamped.Clamped || clamped.Processed != "1-3" || clamped.DocumentPages != 3)
        throw new InvalidOperationException($"1-10 on a 3-page PDF should clamp to 1-3, got {clamped.Processed} clamped={clamped.Clamped}");

    PdfPageWindow partialList = PdfPageSpec.Parse("1,3,100")!.Resolve(7);
    if (!partialList.Clamped || partialList.Processed != "1,3" || !partialList.ZeroBased.SequenceEqual([0, 2]))
        throw new InvalidOperationException("pages past the end of a mixed list should be dropped");

    ExpectThrow("pages is empty.", () => PdfPageSpec.Parse(""));
    ExpectThrow("pages is empty.", () => PdfPageSpec.Parse("   "));
    ExpectThrow("pages contains an empty entry.", () => PdfPageSpec.Parse("1,,2"));
    ExpectThrow("pages contains an empty entry.", () => PdfPageSpec.Parse("1-2,"));
    ExpectThrow("pages value 'abc' is not a page number.", () => PdfPageSpec.Parse("abc"));
    ExpectThrow("pages value '1a' is not a page number.", () => PdfPageSpec.Parse("1a"));
    ExpectThrow("pages range 12-5 is inverted.", () => PdfPageSpec.Parse("12-5"));
    ExpectThrow("pages start at 1.", () => PdfPageSpec.Parse("0"));
    ExpectThrow("pages start at 1.", () => PdfPageSpec.Parse("0-2"));
    ExpectThrow("pages is empty.", () => PdfPageSpec.FromPageNumbers([]));
    ExpectThrow("outside this PDF", () => PdfPageSpec.Parse("5-12")!.Resolve(3));
    ExpectThrow("outside this PDF", () => PdfPageSpec.Parse("8-12")!.Resolve(7));
    ExpectThrow("outside this PDF", () => PdfPageSpec.Parse("100")!.Resolve(7));

    Expect("5-7,12", ReadPages("[12, 5, 6, 7]"), "JSON array");
    Expect("5-12", ReadPages("\"5-12\""), "JSON string");
    Expect("4", ReadPages("4"), "JSON number");
    ExpectThrow("pages array items must be integers.", () => ReadPages("[1, \"x\"]"));
    ExpectThrow("pages is empty.", () => ReadPages("[]"));
    ExpectThrow("pages start at 1.", () => ReadPages("[0, 1]"));
    ExpectThrow("pages must be a string", () => ReadPages("{\"a\":1}"));
}

static async Task TestRenderRequestedPages(byte[] pdf, int documentPages, ILogger<ParallelPdfDownloader> logger)
{
    if (documentPages < 3)
        throw new InvalidOperationException("sample PDF needs at least 3 pages to prove a mid-document range");

    PdfPageWindow first = PdfPageSpec.Parse("1-2")!.Resolve(documentPages);
    int prefix = CountRange(pdf, first.ZeroBased[0], first.ZeroBased.Length);
    if (prefix != 2)
        throw new InvalidOperationException($"first 2 pages rendered {prefix}");

    PdfPageWindow mid = PdfPageSpec.Parse("2-4")!.Resolve(documentPages);
    if (!PdfPageSpan.TryContiguous(mid.ZeroBased, out int midFirst, out int midCount) || midFirst != 1 || midCount != 3)
        throw new InvalidOperationException("pages 2-4 should be one 0-based span starting at 1");
    int midRendered = CountRange(pdf, midFirst, midCount);
    if (midRendered != 3)
        throw new InvalidOperationException($"pages 2-4 rendered {midRendered}");

    PdfPageWindow list = PdfPageSpec.Parse("1,3")!.Resolve(documentPages);
    int listed = CountList(pdf, list.ZeroBased);
    if (listed != 2)
        throw new InvalidOperationException($"page list rendered {listed}");

    using var server = PdfServer.Start(pdf, supportRange: true, redirect: true);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var downloader = new ParallelPdfDownloader(http, logger);
    using PdfRemoteDocument remote = await downloader.AcquireAsync(server.StartUrl, partialOk: true, CancellationToken.None);
    if (remote.Session is null || remote.Mode != "http-range")
        throw new InvalidOperationException("URL page range should stay on the Range session, mode=" + remote.Mode);

    PdfPageWindow remoteMid = PdfPageSpec.Parse("2-3")!.Resolve(remote.Session.PageCount);
    int remoteCount = 0;
    foreach (SKBitmap bitmap in remote.Session.RenderPages(remoteMid.ZeroBased, new RenderOptions(Dpi: 36)))
    {
        using SKBitmap page = bitmap;
        remoteCount++;
        if (page.Width <= 0)
            throw new InvalidOperationException("range render produced an empty page");
    }

    if (remoteCount != remoteMid.ZeroBased.Length)
        throw new InvalidOperationException($"URL range rendered {remoteCount}, expected {remoteMid.ZeroBased.Length}");
    if (remote.BytesFetched >= remote.Length)
        throw new InvalidOperationException(
            $"rendering pages {remoteMid.Processed} fetched the whole file ({remote.BytesFetched}/{remote.Length})");
    if (remote.RangeFetchCount <= 0)
        throw new InvalidOperationException("rendering a page range recorded no Range reads");
    foreach (Hit hit in server.Hits)
    {
        if (hit.Range is null)
            continue;
        if (!hit.PathAndQuery.StartsWith("/final.pdf?sig=changed", StringComparison.Ordinal))
            throw new InvalidOperationException("page-range Range was not sent to the final URL: " + hit.PathAndQuery);
    }
}

static int CountRange(byte[] pdf, int first, int count)
{
    int rendered = 0;
    using var stream = new MemoryStream(pdf, writable: false);
    foreach (SKBitmap bitmap in Conversion.ToImages(
        stream,
        pages: first..(first + count),
        leaveOpen: true,
        options: new RenderOptions(Dpi: 36)))
    {
        using SKBitmap page = bitmap;
        rendered++;
    }

    return rendered;
}

static int CountList(byte[] pdf, int[] zeroBased)
{
    int rendered = 0;
    using var stream = new MemoryStream(pdf, writable: false);
    using PdfSession session = PdfSession.Open(stream, leaveOpen: true);
    foreach (SKBitmap bitmap in session.RenderPages(zeroBased, new RenderOptions(Dpi: 36)))
    {
        using SKBitmap page = bitmap;
        rendered++;
    }

    return rendered;
}

static string ReadPages(string json)
{
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    if (!reader.Read())
        throw new InvalidOperationException("empty JSON");
    return new PdfPageSpecJsonConverter().Read(ref reader, typeof(string), new JsonSerializerOptions())
        ?? throw new InvalidOperationException("converter returned null for " + json);
}

static void Expect(string want, string got, string name)
{
    if (want != got)
        throw new InvalidOperationException($"{name}: expected {want}, got {got}");
}

static void ExpectThrow(string snippet, Action action)
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is ArgumentException or JsonException)
    {
        if (!ex.Message.Contains(snippet, StringComparison.Ordinal))
            throw new InvalidOperationException($"expected '{snippet}' in '{ex.Message}'");
        return;
    }

    throw new InvalidOperationException("expected an error containing " + snippet);
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
