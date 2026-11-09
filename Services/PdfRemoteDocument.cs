using PDFtoImage;

namespace MiniOcr.Services;

/// <summary>
/// PDF obtained from an http(s) URL after redirects have been followed.
/// <see cref="SupportsRange"/> keeps a <see cref="PdfSession"/> over a
/// <see cref="PdfByteRangeStream"/> so later reads fetch only the ranges PDFium asks for.
/// Otherwise <see cref="Buffer"/> is the full download.
/// </summary>
public sealed class PdfRemoteDocument : IDisposable, IAsyncDisposable
{
    private RentedBuffer? _buffer;
    private PdfSession? _session;
    private PdfHttpByteRangeProvider? _provider;
    private int _disposed;

    internal PdfRemoteDocument(
        string mode,
        double elapsedMs,
        Uri finalUri,
        bool supportsRange,
        long length,
        RentedBuffer? buffer,
        PdfSession? session,
        PdfByteRangeStream? rangeStream,
        PdfHttpByteRangeProvider? provider)
    {
        Mode = mode;
        ElapsedMs = elapsedMs;
        FinalUri = finalUri;
        SupportsRange = supportsRange;
        Length = length;
        _buffer = buffer;
        _session = session;
        RangeStream = rangeStream;
        _provider = provider;
        RenderGate = new SemaphoreSlim(1, 1);
    }

    public string Mode { get; }

    public double ElapsedMs { get; }

    /// <summary>URL after redirects. Range requests are sent here, not to the original URL.</summary>
    public Uri FinalUri { get; }

    public bool SupportsRange { get; }

    /// <summary>Content length of the PDF. Not the number of bytes fetched so far.</summary>
    public long Length { get; }

    public PdfByteRangeStream? RangeStream { get; }

    public SemaphoreSlim RenderGate { get; }

    public bool IsPartial => _session is not null;

    public RentedBuffer? Buffer => _buffer;

    public PdfSession? Session => _session;

    public long BytesFetched => RangeStream?.BytesFetched ?? _buffer?.Length ?? 0;

    public int RangeFetchCount => RangeStream?.RangeFetchCount ?? 0;

    /// <summary>Hands the full-download buffer to the caller. Later <see cref="Dispose"/> leaves it alone.</summary>
    public RentedBuffer DetachBuffer()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        RentedBuffer buffer = _buffer ?? throw new InvalidOperationException("This PDF was not fully downloaded.");
        _buffer = null;
        return buffer;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        PdfSession? session = _session;
        _session = null;
        session?.Dispose();

        PdfHttpByteRangeProvider? provider = _provider;
        _provider = null;
        provider?.Dispose();

        RentedBuffer? buffer = _buffer;
        _buffer = null;
        buffer?.Dispose();

        RenderGate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
