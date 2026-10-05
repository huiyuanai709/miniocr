namespace MiniOcr.Services;

/// <summary>
/// Per-job counters for one cluster worker. Page raster and OCR milliseconds are the same
/// sums <see cref="Models.OcrTimings"/> already reports. Post and claim-wait use raw timestamps.
/// </summary>
internal sealed class WorkerStageTotals
{
    private long _renderUs;
    private long _ocrUs;
    private long _postTicks;
    private long _waitTicks;

    public double DownloadMs { get; set; }

    public void AddPage(double rasterMs, double ocrMs)
    {
        AddUs(ref _renderUs, rasterMs);
        AddUs(ref _ocrUs, ocrMs);
    }

    public void AddPost(long startStamp) => StageClock.AddTicks(ref _postTicks, startStamp);

    public void AddWait(long startStamp) => StageClock.AddTicks(ref _waitTicks, startStamp);

    public double RenderMs => Volatile.Read(ref _renderUs) / 1000.0;

    public double OcrMs => Volatile.Read(ref _ocrUs) / 1000.0;

    public double PostMs => StageClock.TicksToMs(Volatile.Read(ref _postTicks));

    public double WaitMs => StageClock.TicksToMs(Volatile.Read(ref _waitTicks));

    private static void AddUs(ref long microseconds, double ms)
    {
        if (ms <= 0)
            return;
        Interlocked.Add(ref microseconds, (long)(ms * 1000.0));
    }
}
