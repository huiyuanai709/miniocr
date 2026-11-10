using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using PDFtoImage;
using PDFtoImage.Exceptions;

namespace MiniOcr.Services;

public sealed class ParallelPdfDownloader
{
    public const long MaxBytes = 300L * 1024 * 1024;
    private const int MinChunkBytes = 1 * 1024 * 1024;
    private const int DefaultParallelism = 8;

    private readonly HttpClient _http;
    private readonly ArrayPool<byte> _pool;
    private readonly ILogger<ParallelPdfDownloader> _logger;

    public ParallelPdfDownloader(HttpClient http, ILogger<ParallelPdfDownloader> logger)
    {
        _http = http;
        _pool = ArrayPool<byte>.Shared;
        _logger = logger;
    }

    public sealed record DownloadResult(RentedBuffer Buffer, string Mode, double ElapsedMs);

    public Task<DownloadResult> DownloadAsync(string url, CancellationToken ct) =>
        DownloadAsync(url, ct, prepare: null);

    public async Task<DownloadResult> DownloadAsync(
        string url,
        CancellationToken ct,
        Action<HttpRequestMessage>? prepare)
    {
        PdfRemoteDocument doc = await AcquireAsync(url, partialOk: false, ct, prepare).ConfigureAwait(false);
        try
        {
            RentedBuffer buffer = doc.DetachBuffer();
            return new DownloadResult(buffer, doc.Mode, doc.ElapsedMs);
        }
        finally
        {
            await doc.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens a remote PDF. Redirects are followed first; Range requests go to the final URL
    /// (signed query parameters on that hop are kept). When the server accepts Range and
    /// <paramref name="partialOk"/> is true, the result is a <see cref="PdfSession"/> over a
    /// <see cref="PdfByteRangeStream"/> and the rest of the file is fetched only as pages are read.
    /// Otherwise the result is a full download (<c>http-full-fallback</c> when Range is missing,
    /// <c>http-range-full</c> when the caller asked for every byte of a Range-capable URL).
    /// </summary>
    public Task<PdfRemoteDocument> AcquireAsync(string url, bool partialOk, CancellationToken ct) =>
        AcquireAsync(url, partialOk, ct, prepare: null);

    public async Task<PdfRemoteDocument> AcquireAsync(
        string url,
        bool partialOk,
        CancellationToken ct,
        Action<HttpRequestMessage>? prepare)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("url must be an absolute http(s) URL.");
        }

        Stopwatch sw = Stopwatch.StartNew();
        Uri final = await ResolveFinalUriAsync(uri, prepare, ct).ConfigureAwait(false);
        if (!UriEquals(uri, final))
        {
            _logger.LogInformation(
                "PDF URL redirected; later Range requests use host {Host}",
                final.Host);
        }

        // Per-request headers (cluster auth) cannot be attached by PdfHttpByteRangeProvider.
        if (prepare is not null)
            return await AcquirePreparedFullAsync(final, prepare, sw, ct).ConfigureAwait(false);

        try
        {
            PdfHttpByteRangeProvider? provider = await PdfHttpByteRangeProvider.CreateAsync(
                final,
                _http,
                allowFullDownloadFallback: true,
                ct).ConfigureAwait(false);
            try
            {
                if (provider.Length <= 0 || provider.Length > MaxBytes)
                {
                    throw new InvalidOperationException(
                        $"PDF exceeds {MaxBytes} byte cap ({provider.Length} bytes).");
                }

                if (!provider.SupportsRange)
                {
                    RentedBuffer buffer = CopyProvider(provider);
                    sw.Stop();
                    _logger.LogInformation(
                        "PDF server does not accept Range; full download host={Host} bytes={Bytes} ms={Ms:F0}",
                        final.Host,
                        buffer.Length,
                        sw.Elapsed.TotalMilliseconds);
                    PdfHttpByteRangeProvider? consumed = provider;
                    provider = null!;
                    consumed.Dispose();
                    return new PdfRemoteDocument(
                        "http-full-fallback",
                        sw.Elapsed.TotalMilliseconds,
                        final,
                        supportsRange: false,
                        buffer.Length,
                        buffer,
                        session: null,
                        rangeStream: null,
                        provider: null);
                }

                if (!partialOk)
                {
                    long length = provider.Length;
                    provider.Dispose();
                    provider = null!;
                    try
                    {
                        return await AcquireRangedFullAsync(final, length, prepare: null, sw, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsAcquireFallback(ex, ct))
                    {
                        _logger.LogWarning(
                            ex,
                            "Parallel Range download failed for {Host}; falling back to a single stream",
                            final.Host);
                        return await AcquireSingleDocumentAsync(
                            final, prepare: null, sw, ct, "http-range-single-fallback").ConfigureAwait(false);
                    }
                }

                PdfByteRangeStream stream = await PdfByteRangeStream.OpenAsync(
                    provider,
                    cancellationToken: ct).ConfigureAwait(false);
                PdfSession session;
                try
                {
                    session = PdfSession.Open(stream, leaveOpen: false);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                sw.Stop();
                _logger.LogInformation(
                    "PDF HTTP Range session host={Host} contentLength={Bytes} probed={Probed} reads={Reads} ms={Ms:F0}",
                    final.Host,
                    provider.Length,
                    stream.BytesFetched,
                    stream.RangeFetchCount,
                    sw.Elapsed.TotalMilliseconds);
                PdfHttpByteRangeProvider owned = provider;
                provider = null!;
                return new PdfRemoteDocument(
                    "http-range",
                    sw.Elapsed.TotalMilliseconds,
                    final,
                    supportsRange: true,
                    owned.Length,
                    buffer: null,
                    session,
                    stream,
                    owned);
            }
            finally
            {
                provider?.Dispose();
            }
        }
        catch (Exception ex) when (IsAcquireFallback(ex, ct))
        {
            _logger.LogWarning(ex, "HTTP Range open failed for {Host}; falling back to a full download", final.Host);
            return await AcquireSingleDocumentAsync(final, prepare: null, sw, ct, "http-range-single-fallback").ConfigureAwait(false);
        }
    }

    private static bool IsAcquireFallback(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;
        return ex is PdfHttpRangeException
            or PdfByteRangeException
            or HttpRequestException
            or IOException
            or TimeoutException
            or TaskCanceledException;
    }

    /// <summary>
    /// Follows redirects without a Range header and returns the URL that actually answered.
    /// Subsequent Range requests must use this URI: a signed query string is often different.
    /// </summary>
    private async Task<Uri> ResolveFinalUriAsync(Uri uri, Action<HttpRequestMessage>? prepare, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        prepare?.Invoke(request);
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        Uri final = response.RequestMessage?.RequestUri ?? uri;
        if (response.StatusCode is HttpStatusCode.NotFound)
            return final;
        // Body is unused. Disposing here aborts it so a redirect hop is not a full download.
        return final;
    }

    private async Task<PdfRemoteDocument> AcquirePreparedFullAsync(
        Uri final,
        Action<HttpRequestMessage> prepare,
        Stopwatch sw,
        CancellationToken ct)
    {
        long? contentLength = null;
        bool acceptRanges = false;
        try
        {
            using HttpRequestMessage head = new(HttpMethod.Head, final);
            prepare(head);
            using HttpResponseMessage headResp = await _http.SendAsync(
                head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            Uri afterHead = headResp.RequestMessage?.RequestUri ?? final;
            if (!UriEquals(afterHead, final))
                final = afterHead;
            if (headResp.IsSuccessStatusCode)
            {
                contentLength = headResp.Content.Headers.ContentLength;
                acceptRanges = SupportsAcceptRanges(headResp);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "HEAD failed for {Host}; falling back to GET", final.Host);
        }

        if (contentLength is > MaxBytes)
            throw new InvalidOperationException($"PDF exceeds {MaxBytes} byte cap ({contentLength} bytes).");

        if (contentLength is > 0 && acceptRanges)
        {
            try
            {
                return await AcquireRangedFullAsync(final, contentLength.Value, prepare, sw, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Ranged download failed; falling back to single stream for {Host}", final.Host);
                return await AcquireSingleDocumentAsync(final, prepare, sw, ct, "single-presized-fallback").ConfigureAwait(false);
            }
        }

        string mode = contentLength is > 0 ? "single-presized" : "single-grow";
        return await AcquireSingleDocumentAsync(final, prepare, sw, ct, mode).ConfigureAwait(false);
    }

    private async Task<PdfRemoteDocument> AcquireRangedFullAsync(
        Uri final,
        long length,
        Action<HttpRequestMessage>? prepare,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (length <= 0 || length > MaxBytes)
            throw new InvalidOperationException($"PDF exceeds {MaxBytes} byte cap ({length} bytes).");

        RentedBuffer buffer = await DownloadRangedAsync(final, length, prepare, ct).ConfigureAwait(false);
        sw.Stop();
        _logger.LogInformation(
            "PDF full download via HTTP Range host={Host} bytes={Bytes} ms={Ms:F0}",
            final.Host,
            buffer.Length,
            sw.Elapsed.TotalMilliseconds);
        return new PdfRemoteDocument(
            "http-range-full",
            sw.Elapsed.TotalMilliseconds,
            final,
            supportsRange: true,
            buffer.Length,
            buffer,
            session: null,
            rangeStream: null,
            provider: null);
    }

    private async Task<PdfRemoteDocument> AcquireSingleDocumentAsync(
        Uri final,
        Action<HttpRequestMessage>? prepare,
        Stopwatch sw,
        CancellationToken ct,
        string mode)
    {
        RentedBuffer buffer = await DownloadSingleAsync(final, knownLength: null, prepare, ct).ConfigureAwait(false);
        sw.Stop();
        return new PdfRemoteDocument(
            mode,
            sw.Elapsed.TotalMilliseconds,
            final,
            supportsRange: false,
            buffer.Length,
            buffer,
            session: null,
            rangeStream: null,
            provider: null);
    }

    private RentedBuffer CopyProvider(PdfHttpByteRangeProvider provider)
    {
        int size = checked((int)provider.Length);
        byte[] rented = _pool.Rent(size);
        try
        {
            provider.Read(0, rented, 0, size);
            return new RentedBuffer(rented, size, _pool);
        }
        catch
        {
            _pool.Return(rented);
            throw;
        }
    }

    private static bool UriEquals(Uri left, Uri right) =>
        Uri.Compare(left, right, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;

    private static bool SupportsAcceptRanges(HttpResponseMessage resp)
    {
        if (resp.Headers.AcceptRanges.Contains("none", StringComparer.OrdinalIgnoreCase))
            return false;
        if (resp.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase))
            return true;
        return resp.Headers.AcceptRanges.Count > 0;
    }

    private async Task<RentedBuffer> DownloadRangedAsync(
        Uri uri,
        long length,
        Action<HttpRequestMessage>? prepare,
        CancellationToken ct)
    {
        int size = checked((int)length);
        byte[] rented = _pool.Rent(size);
        bool transferOwnership = false;
        try
        {
            int parallelism = Math.Clamp(Environment.ProcessorCount, 2, DefaultParallelism);
            int chunkSize = Math.Max(MinChunkBytes, (size + parallelism - 1) / parallelism);
            int chunkCount = (size + chunkSize - 1) / chunkSize;
            parallelism = Math.Min(parallelism, chunkCount);

            using SemaphoreSlim gate = new(parallelism, parallelism);
            Task[] tasks = new Task[chunkCount];

            for (int i = 0; i < chunkCount; i++)
            {
                int index = i;
                long start = (long)index * chunkSize;
                long end = Math.Min(start + chunkSize, length) - 1;
                tasks[i] = Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using HttpRequestMessage req = new(HttpMethod.Get, uri);
                        prepare?.Invoke(req);
                        req.Headers.Range = new RangeHeaderValue(start, end);
                        using HttpResponseMessage resp = await _http.SendAsync(
                            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        if (resp.StatusCode != HttpStatusCode.PartialContent)
                        {
                            throw new InvalidOperationException(
                                $"Expected 206 for range {start}-{end}, got {(int)resp.StatusCode}.");
                        }

                        int offset = (int)start;
                        int remaining = (int)(end - start + 1);
                        Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await using (stream.ConfigureAwait(false))
                        {
                            while (remaining > 0)
                            {
                                int read = await stream.ReadAsync(rented.AsMemory(offset, remaining), ct)
                                    .ConfigureAwait(false);
                                if (read == 0)
                                    throw new EndOfStreamException("Unexpected EOF in ranged download.");
                                offset += read;
                                remaining -= read;
                            }
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            transferOwnership = true;
            return new RentedBuffer(rented, size, _pool);
        }
        finally
        {
            if (!transferOwnership)
                _pool.Return(rented);
        }
    }

    private async Task<RentedBuffer> DownloadSingleAsync(
        Uri uri,
        long? knownLength,
        Action<HttpRequestMessage>? prepare,
        CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, uri);
        prepare?.Invoke(req);
        using HttpResponseMessage resp = await _http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? length = knownLength ?? resp.Content.Headers.ContentLength;
        if (length is > MaxBytes)
            throw new InvalidOperationException($"PDF exceeds {MaxBytes} byte cap ({length} bytes).");

        Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (length is > 0)
            {
                int size = checked((int)length.Value);
                byte[] rented = _pool.Rent(size);
                bool transferOwnership = false;
                try
                {
                    int offset = 0;
                    int remaining = size;
                    while (remaining > 0)
                    {
                        int read = await stream.ReadAsync(rented.AsMemory(offset, remaining), ct)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            size = offset;
                            break;
                        }
                        offset += read;
                        remaining -= read;
                    }
                    transferOwnership = true;
                    return new RentedBuffer(rented, size, _pool);
                }
                finally
                {
                    if (!transferOwnership)
                        _pool.Return(rented);
                }
            }

            int capacity = 256 * 1024;
            byte[] buffer = _pool.Rent(capacity);
            int written = 0;
            bool owned = false;
            try
            {
                while (true)
                {
                    if (written == buffer.Length)
                    {
                        if (written >= MaxBytes)
                            throw new InvalidOperationException($"PDF exceeds {MaxBytes} byte cap.");
                        int newCap = (int)Math.Min((long)buffer.Length * 2, MaxBytes);
                        if (newCap <= buffer.Length)
                            throw new InvalidOperationException($"PDF exceeds {MaxBytes} byte cap.");
                        byte[] grown = _pool.Rent(newCap);
                        Buffer.BlockCopy(buffer, 0, grown, 0, written);
                        _pool.Return(buffer);
                        buffer = grown;
                    }

                    int read = await stream.ReadAsync(buffer.AsMemory(written, buffer.Length - written), ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;
                    written += read;
                    if (written > MaxBytes)
                        throw new InvalidOperationException($"PDF exceeds {MaxBytes} byte cap.");
                }

                owned = true;
                return new RentedBuffer(buffer, written, _pool);
            }
            finally
            {
                if (!owned)
                    _pool.Return(buffer);
            }
        }
    }
}
