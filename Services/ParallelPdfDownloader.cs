using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;

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
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("url must be an absolute http(s) URL.");
        }

        Stopwatch sw = Stopwatch.StartNew();

        long? contentLength = null;
        bool acceptRanges = false;

        try
        {
            using HttpRequestMessage head = new(HttpMethod.Head, uri);
            prepare?.Invoke(head);
            using HttpResponseMessage headResp = await _http.SendAsync(
                head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (headResp.IsSuccessStatusCode)
            {
                contentLength = headResp.Content.Headers.ContentLength;
                acceptRanges = SupportsAcceptRanges(headResp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HEAD failed for {Url}; falling back to GET", url);
        }

        if (contentLength is > MaxBytes)
            throw new InvalidOperationException($"PDF exceeds {MaxBytes} byte cap ({contentLength} bytes).");

        DownloadResult result;
        if (contentLength is > 0 && acceptRanges)
        {
            try
            {
                RentedBuffer buffer = await DownloadRangedAsync(uri, contentLength.Value, prepare, ct).ConfigureAwait(false);
                sw.Stop();
                result = new DownloadResult(buffer, "parallel-ranges", sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ranged download failed; falling back to single stream for {Uri}", uri);
                RentedBuffer buffer = await DownloadSingleAsync(uri, contentLength, prepare, ct).ConfigureAwait(false);
                sw.Stop();
                result = new DownloadResult(buffer, "single-presized-fallback", sw.Elapsed.TotalMilliseconds);
            }
        }
        else
        {
            RentedBuffer buffer = await DownloadSingleAsync(uri, contentLength, prepare, ct).ConfigureAwait(false);
            sw.Stop();
            string mode = contentLength is > 0 ? "single-presized" : "single-grow";
            result = new DownloadResult(buffer, mode, sw.Elapsed.TotalMilliseconds);
        }

        return result;
    }

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
                        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
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
