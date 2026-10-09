using System.Collections.Concurrent;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// One in-flight distributed OCR job. Page text is committed here; the pipeline owns NER.
/// </summary>
public sealed class ClusterJob
{
    /// <summary>
    /// While a joined node is still fetching the PDF, the coordinator keeps its local window
    /// hold so it does not eat a short document alone. A stuck download cannot hold forever.
    /// At or above <see cref="DownloadHoldPageLimit"/> pages this hold ends with the join grace:
    /// the coordinator cannot finish that document during the download.
    /// </summary>
    public static readonly TimeSpan DownloadHold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// At or above this many pages the coordinator resumes after join grace even if workers
    /// are still downloading. A 463-page scan cannot be finished in that window, and holding
    /// the whole download leaves the coordinator idle.
    /// </summary>
    public const int DownloadHoldPageLimit = 48;

    private readonly object _acceptGate = new();
    private readonly object _readGate = new();
    private readonly TaskCompletionSource _readersDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CancellationTokenSource> _readCancels = [];
    private readonly ConcurrentDictionary<string, long> _downloading = new(StringComparer.Ordinal);
    private Action<OcrPageResult>? _onAccepted;
    private int _readers;
    private int _rejectReads;
    private int _finished;
    private int _graceElapsed;
    private int _forceReleaseHold;

    public ClusterJob(
        string id,
        ClusterPageScheduler scheduler,
        byte[] pdf,
        int pdfLength,
        int pageCount,
        int dpi,
        string? sourceUrl)
    {
        Id = id;
        Scheduler = scheduler;
        Pdf = pdf;
        PdfLength = pdfLength;
        PageCount = pageCount;
        Dpi = dpi;
        SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? "" : sourceUrl.Trim();
        Started = DateTimeOffset.UtcNow;
        LastProgressAt = Started;
    }

    public string Id { get; }
    public ClusterPageScheduler Scheduler { get; }
    public byte[] Pdf { get; }
    public int PdfLength { get; }
    public int PageCount { get; }
    public int Dpi { get; }
    /// <summary>Original http(s) URL the coordinator downloaded, when the job came from a link.</summary>
    public string SourceUrl { get; }
    /// <summary>Absolute path of the PDF in the shared directory. Empty when workers must download.</summary>
    public string SharedPath { get; set; } = "";
    public DateTimeOffset Started { get; }
    public bool ExpectRemote { get; set; }
    public ConcurrentDictionary<string, byte> Joined { get; } = new(StringComparer.Ordinal);
    internal int LastProgressDone;
    internal int LastProgressBucket;
    internal int LastNerDone;
    internal int LastNerBucket;
    internal DateTimeOffset LastProgressAt;
    /// <summary>Set when this job distributes text NER. Null keeps NER on the coordinator pipeline.</summary>
    public ClusterNerScheduler? Ner { get; set; }
    internal bool DeferProgress;
    internal object ProgressGate { get; } = new();
    public bool IsFinished => Volatile.Read(ref _finished) == 1;
    public bool GraceElapsed => Volatile.Read(ref _graceElapsed) == 1;

    public void SetAccepted(Action<OcrPageResult> onAccepted) => _onAccepted = onAccepted;

    public bool TryAccept(string batchId, OcrPageResult page)
    {
        lock (_acceptGate)
        {
            if (!Scheduler.TryCommit(batchId, page.Page))
                return false;
            Ner?.AddPage(page);
            _onAccepted?.Invoke(page);
            return true;
        }
    }

    /// <summary>
    /// A page finished before any OCR lease (text layer). It is already marked done
    /// on the scheduler, so NER and the pipeline see it without a batch commit.
    /// </summary>
    public void AcceptPrepared(OcrPageResult page)
    {
        lock (_acceptGate)
        {
            int index = page.Page - 1;
            if ((uint)index >= (uint)PageCount || !Scheduler.IsPageDone(index))
                return;
            Ner?.AddPage(page);
            _onAccepted?.Invoke(page);
        }
    }

    /// <summary>Block until any in-flight <see cref="TryAccept"/> has finished mutating page state.</summary>
    public void DrainAccepts()
    {
        lock (_acceptGate)
        {
        }
    }

    public void MarkFinished() => Volatile.Write(ref _finished, 1);

    public void MarkGraceElapsed() => Volatile.Write(ref _graceElapsed, 1);

    public void MarkDownloading(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
            _downloading[nodeId] = DateTimeOffset.UtcNow.UtcTicks;
    }

    public void ClearDownloading(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
            _downloading.TryRemove(nodeId, out _);
    }

    /// <summary>Deadline takeover: stop holding the local window for downloaders.</summary>
    public void ForceReleaseDownloadHold()
    {
        Volatile.Write(ref _forceReleaseHold, 1);
        Volatile.Write(ref _graceElapsed, 1);
        _downloading.Clear();
    }

    public bool ShouldHoldLocal(DateTimeOffset now)
    {
        if (Volatile.Read(ref _forceReleaseHold) == 1)
            return false;
        if (!GraceElapsed)
            return true;
        if (PageCount >= DownloadHoldPageLimit)
            return false;
        return AnyFreshDownload(now, DownloadHold);
    }

    public bool AnyFreshDownload(DateTimeOffset now, TimeSpan maxHold)
    {
        long cutoff = now.Subtract(maxHold).UtcTicks;
        foreach ((string node, long started) in _downloading)
        {
            if (started >= cutoff)
                return true;
            _downloading.TryRemove(node, out _);
        }

        return false;
    }

    public PdfReadLease? TryEnterPdfRead()
    {
        if (Volatile.Read(ref _rejectReads) == 1)
            return null;
        var cts = new CancellationTokenSource();
        Interlocked.Increment(ref _readers);
        if (Volatile.Read(ref _rejectReads) == 1)
        {
            cts.Dispose();
            ExitRead();
            return null;
        }

        lock (_readGate)
            _readCancels.Add(cts);
        if (Volatile.Read(ref _rejectReads) == 1)
        {
            cts.Cancel();
            RemoveRead(cts);
            ExitRead();
            return null;
        }

        return new PdfReadLease(this, cts);
    }

    public void StopNewPdfReads()
    {
        Volatile.Write(ref _rejectReads, 1);
        CancellationTokenSource[] copy;
        lock (_readGate)
            copy = _readCancels.ToArray();
        foreach (CancellationTokenSource cts in copy)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async Task WaitForPdfReadersAsync(TimeSpan timeout)
    {
        if (Volatile.Read(ref _readers) <= 0)
            return;
        Task finished = _readersDrained.Task;
        Task delay = Task.Delay(timeout);
        await Task.WhenAny(finished, delay).ConfigureAwait(false);
    }

    private void ExitRead()
    {
        if (Interlocked.Decrement(ref _readers) <= 0)
            _readersDrained.TrySetResult();
    }

    private void RemoveRead(CancellationTokenSource cts)
    {
        lock (_readGate)
            _readCancels.Remove(cts);
        cts.Dispose();
    }

    public sealed class PdfReadLease : IDisposable
    {
        private ClusterJob? _job;
        private CancellationTokenSource? _cts;

        internal PdfReadLease(ClusterJob job, CancellationTokenSource cts)
        {
            _job = job;
            _cts = cts;
            Token = cts.Token;
        }

        public CancellationToken Token { get; }

        public void Dispose()
        {
            ClusterJob? job = Interlocked.Exchange(ref _job, null);
            CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
            if (job is null || cts is null)
                return;
            job.RemoveRead(cts);
            job.ExitRead();
        }
    }
}
