namespace MiniOcr.Services;

public enum ClusterClaimKind
{
    Batch = 0,
    Wait = 1,
    Done = 2,
}

public sealed class ClusterClaim
{
    public ClusterClaimKind Kind { get; init; }
    public string BatchId { get; init; } = "";
    /// <summary>1-based page numbers.</summary>
    public int[] Pages { get; init; } = [];
    public int LeaseMs { get; init; }
    public bool Speculative { get; init; }
    public long Version { get; init; }
    public int RetryAfterMs { get; init; } = 200;
}

public sealed class ClusterScheduleOptions
{
    public int PageCount { get; init; }
    public string LocalNodeId { get; init; } = "local";
    /// <summary>0 = one batch sized from the node's capacity (clamped 1–16).</summary>
    public int PagesPerBatch { get; init; }
    public int LeaseFloorMs { get; init; } = 15_000;
    public int PageTimeoutMs { get; init; } = 20_000;
    public int LeaseCapMs { get; init; } = 180_000;
    public int SpeculativeTailPages { get; init; } = 4;
    public int ExpectedNodes { get; init; } = 1;
}

public sealed class ClusterNodeLoad
{
    public string NodeId { get; init; } = "";
    public int PagesCommitted { get; set; }
    public int InFlight { get; set; }
}

/// <summary>A lease whose deadline passed. Recorded for logging; does not change scheduling.</summary>
public sealed class ClusterLeaseExpiry
{
    public string BatchId { get; init; } = "";
    public string NodeId { get; init; } = "";
    /// <summary>1-based pages returned to the pending queue. Empty when another lease still holds them.</summary>
    public int[] Pages { get; init; } = [];
    public bool Speculative { get; init; }
}

public sealed class ClusterScheduleSnapshot
{
    public int Done { get; init; }
    public int Pending { get; init; }
    public int LeasedPages { get; init; }
    public bool Complete { get; init; }
    public long Version { get; init; }
    public List<ClusterNodeLoad> Nodes { get; init; } = [];
}

/// <summary>
/// Pull queue for one PDF. Nodes claim page batches sized by capacity; expired or
/// abandoned leases return to the queue. Near the end, idle nodes may speculatively
/// copy the tail so a slow last batch cannot hold the job.
/// </summary>
public sealed class ClusterPageScheduler
{
    private readonly object _gate = new();
    private readonly int _pageCount;
    private readonly string _localNodeId;
    private readonly int _pagesPerBatch;
    private readonly int _leaseFloorMs;
    private readonly int _pageTimeoutMs;
    private readonly int _leaseCapMs;
    private readonly int _speculativeTail;
    private readonly PageState[] _pages;
    private readonly Queue<int> _pending;
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _caps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _committed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pace> _pace = new(StringComparer.Ordinal);
    private readonly List<TaskCompletionSource<bool>> _waiters = [];
    private readonly List<ClusterLeaseExpiry> _expiries = [];
    private int _expectedNodes;
    private int _done;
    private int _batchSeq;
    private long _version;
    private bool _holdLocalWindow;
    /// <summary>0-based page that blocks the next NER group. -1 when NER is not waiting on a hole.</summary>
    private int _nerBlocker = -1;

    public ClusterPageScheduler(ClusterScheduleOptions options)
    {
        if (options.PageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "PageCount must be positive.");
        _pageCount = options.PageCount;
        _localNodeId = string.IsNullOrWhiteSpace(options.LocalNodeId) ? "local" : options.LocalNodeId;
        _pagesPerBatch = options.PagesPerBatch;
        _leaseFloorMs = Math.Max(1, options.LeaseFloorMs);
        _pageTimeoutMs = Math.Max(1, options.PageTimeoutMs);
        _leaseCapMs = Math.Max(_leaseFloorMs, options.LeaseCapMs);
        _speculativeTail = Math.Max(1, options.SpeculativeTailPages);
        _expectedNodes = Math.Max(1, options.ExpectedNodes);
        _pages = new PageState[_pageCount];
        _pending = new Queue<int>(_pageCount);
        for (int i = 0; i < _pageCount; i++)
        {
            _pages[i] = new PageState();
            _pending.Enqueue(i);
            _pages[i].Queued = true;
        }
    }

    public string LocalNodeId => _localNodeId;
    public bool IsComplete
    {
        get { lock (_gate) return _done == _pageCount; }
    }

    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public void SetCapacity(string nodeId, int capacity)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return;
        lock (_gate)
        {
            _caps[nodeId] = Math.Max(1, capacity);
            SignalCore();
        }
    }

    public void SetExpectedNodes(int count)
    {
        lock (_gate)
        {
            _expectedNodes = Math.Max(1, count);
            SignalCore();
        }
    }

    /// <summary>
    /// When true, the local node will not take more than one window of pages while work
    /// is still pending. Gives remote pullers time to join a short job.
    /// </summary>
    public void SetHoldLocalWindow(bool hold)
    {
        lock (_gate)
        {
            if (_holdLocalWindow == hold)
                return;
            _holdLocalWindow = hold;
            SignalCore();
        }
    }

    /// <summary>
    /// The earliest OCR page the NER grouper is still waiting on. An idle node may copy it
    /// once the owner's lease has run much longer than that node's measured page time.
    /// </summary>
    public void SetNerBlocker(int? oneBasedPage)
    {
        lock (_gate)
        {
            int next = oneBasedPage is int page && page >= 1 && page <= _pageCount ? page - 1 : -1;
            if (_nerBlocker == next)
                return;
            _nerBlocker = next;
            SignalCore();
        }
    }

    public ClusterClaim Claim(string nodeId, int maxPages, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("nodeId is required.", nameof(nodeId));
        lock (_gate)
        {
            ReapCore(now);
            if (_done == _pageCount)
                return DoneClaim();

            int cap = CapacityOf(nodeId);
            int inFlight = InFlightCore(nodeId);
            int room = cap - inFlight;
            bool local = nodeId == _localNodeId;
            if (local && _holdLocalWindow && _pending.Count > 0)
            {
                int window = Math.Max(1, cap);
                int owned = CommittedCore(nodeId) + inFlight;
                if (owned >= window)
                    return WaitClaim();
            }

            if (room <= 0)
                return WaitClaim();

            int blocked = TrySpeculativeBlocker(nodeId, now);
            if (blocked >= 0)
                return LeasePages(nodeId, [blocked], speculative: true, now);

            int want = Math.Min(room, AdaptiveBatch(nodeId, cap));
            if (maxPages > 0)
                want = Math.Min(want, maxPages);
            want = Shrink(want);
            if (want < 1)
                want = 1;

            if (_pending.Count > 0)
            {
                int[] taken = Dequeue(want);
                if (taken.Length > 0)
                    return LeasePages(nodeId, taken, speculative: false, now);
            }

            if (_done == _pageCount)
                return DoneClaim();

            int undone = _pageCount - _done;
            if (undone > 0 && undone <= _speculativeTail && room > 0)
            {
                int[] copies = PickSpeculative(nodeId, Math.Min(room, want));
                if (copies.Length > 0)
                    return LeasePages(nodeId, copies, speculative: true, now);
            }

            return _done == _pageCount ? DoneClaim() : WaitClaim();
        }
    }

    /// <summary>First successful commit of a page wins. Duplicates (speculative or retry) return false.</summary>
    public bool TryCommit(string batchId, int pageNumber) =>
        TryCommit(batchId, pageNumber, DateTimeOffset.UtcNow);

    /// <summary>First successful commit of a page wins. Duplicates (speculative or retry) return false.</summary>
    public bool TryCommit(string batchId, int pageNumber, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(batchId, out Lease? lease))
                return false;
            int index = pageNumber - 1;
            if ((uint)index >= (uint)_pageCount || !lease.Pages.Contains(index))
                return false;
            if (_pages[index].Done)
                return false;

            NotePace(lease, now);
            _pages[index].Done = true;
            _done++;
            _committed[lease.NodeId] = CommittedCore(lease.NodeId) + 1;
            DetachPage(index);
            SignalCore();
            return true;
        }
    }

    /// <summary>Drop a lease and requeue any of its pages that nobody else is still running.</summary>
    public int[] ReleaseBatch(string batchId)
    {
        lock (_gate)
        {
            int[] released = ReleaseCore(batchId);
            if (released.Length > 0)
                SignalCore();
            return released;
        }
    }

    public void Reap(DateTimeOffset now)
    {
        lock (_gate)
        {
            int before = _pending.Count;
            int leases = _leases.Count;
            ReapCore(now);
            if (_pending.Count != before || _leases.Count != leases)
                SignalCore();
        }
    }

    /// <summary>Return every page leased to <paramref name="nodeId"/> that is not already done.</summary>
    public void DropNode(string nodeId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return;
        lock (_gate)
        {
            List<string> ids = [];
            foreach ((string id, Lease lease) in _leases)
            {
                if (lease.NodeId == nodeId)
                    ids.Add(id);
            }

            bool changed = false;
            foreach (string id in ids)
            {
                if (ReleaseCore(id).Length > 0)
                    changed = true;
            }

            if (changed)
                SignalCore();
            _ = now;
        }
    }

    /// <summary>Drop every remote lease so the local node can finish the job.</summary>
    public void TakeOverLocal(DateTimeOffset now)
    {
        lock (_gate)
        {
            List<string> ids = [];
            foreach ((string id, Lease lease) in _leases)
            {
                if (lease.NodeId != _localNodeId)
                    ids.Add(id);
            }

            bool changed = ids.Count > 0;
            foreach (string id in ids)
                ReleaseCore(id);
            _holdLocalWindow = false;
            if (changed)
                SignalCore();
            _ = now;
        }
    }

    public ClusterScheduleSnapshot Snapshot()
    {
        lock (_gate)
            return SnapshotCore();
    }

    public Task WaitForChangeAsync(long seenVersion, TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            if (_version != seenVersion || _done == _pageCount)
                return Task.CompletedTask;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(tcs);
        }

        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return tcs.Task;
        }

        CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        CancellationTokenRegistration reg = timeoutCts.Token.Register(static state =>
        {
            ((TaskCompletionSource<bool>)state!).TrySetResult(false);
        }, tcs);

        return tcs.Task.ContinueWith(t =>
        {
            reg.Dispose();
            timeoutCts.Dispose();
            lock (_gate)
                _waiters.Remove(tcs);
            return t;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    private ClusterScheduleSnapshot SnapshotCore()
    {
        var loads = new Dictionary<string, ClusterNodeLoad>(StringComparer.Ordinal);
        foreach ((string node, int pages) in _committed)
        {
            loads[node] = new ClusterNodeLoad
            {
                NodeId = node,
                PagesCommitted = pages,
                InFlight = 0,
            };
        }

        int leased = 0;
        foreach (Lease lease in _leases.Values)
        {
            if (!loads.TryGetValue(lease.NodeId, out ClusterNodeLoad? load))
            {
                load = new ClusterNodeLoad { NodeId = lease.NodeId };
                loads[lease.NodeId] = load;
            }

            foreach (int index in lease.Pages)
            {
                if (_pages[index].Done)
                    continue;
                load.InFlight++;
                leased++;
            }
        }

        return new ClusterScheduleSnapshot
        {
            Done = _done,
            Pending = _pending.Count,
            LeasedPages = leased,
            Complete = _done == _pageCount,
            Version = _version,
            Nodes = loads.Values.ToList(),
        };
    }

    private int[] Dequeue(int want)
    {
        List<int> taken = new(want);
        if (want > 0 && TryUnqueueBlocker(out int blocker))
            taken.Add(blocker);
        int guard = _pending.Count;
        while (taken.Count < want && _pending.Count > 0 && guard-- >= 0)
        {
            int index = _pending.Dequeue();
            _pages[index].Queued = false;
            if (_pages[index].Done || IsLeased(index))
                continue;
            taken.Add(index);
        }

        return taken.ToArray();
    }

    private bool TryUnqueueBlocker(out int index)
    {
        index = _nerBlocker;
        if (index < 0 || _pages[index].Done || !_pages[index].Queued || IsLeased(index))
            return false;
        int n = _pending.Count;
        int[] buf = new int[n];
        for (int i = 0; i < n; i++)
            buf[i] = _pending.Dequeue();
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            if (!found && buf[i] == index)
            {
                found = true;
                continue;
            }

            _pending.Enqueue(buf[i]);
        }

        if (!found)
            return false;
        _pages[index].Queued = false;
        return true;
    }

    /// <summary>
    /// Copy of the NER-blocking page when it is already leased and has been outstanding
    /// much longer than the owner's measured time for one page. At most one extra copy.
    /// </summary>
    private int TrySpeculativeBlocker(string nodeId, DateTimeOffset now)
    {
        int index = _nerBlocker;
        if (index < 0 || _pages[index].Done)
            return -1;
        if (NodeHasPage(nodeId, index) || LeaseCount(index) == 0 || LeaseCount(index) >= 2)
            return -1;
        Lease? owner = null;
        foreach (Lease lease in _leases.Values)
        {
            if (!lease.Speculative && lease.Pages.Contains(index))
            {
                owner = lease;
                break;
            }
        }

        if (owner is null || owner.NodeId == nodeId)
            return -1;
        double ageMs = (now - owner.Created).TotalMilliseconds;
        double threshold = Math.Max(2_500, ExpectedPageMs(owner.NodeId) * 2.0);
        return ageMs >= threshold ? index : -1;
    }

    private double ExpectedPageMs(string nodeId)
    {
        if (!_pace.TryGetValue(nodeId, out Pace? pace) || pace.Pages < 1 || pace.Seconds <= 0)
            return 4_000;
        double pps = pace.Pages / pace.Seconds;
        if (pps <= 0.01)
            return 60_000;
        return 1000.0 / pps;
    }

    private void NotePace(Lease lease, DateTimeOffset now)
    {
        if (!_pace.TryGetValue(lease.NodeId, out Pace? pace))
        {
            pace = new Pace();
            _pace[lease.NodeId] = pace;
        }

        pace.Pages++;
        if (!string.Equals(pace.OpenBatch, lease.BatchId, StringComparison.Ordinal))
        {
            pace.OpenBatch = lease.BatchId;
            pace.Seconds += Math.Max(0.05, (now - lease.Created).TotalSeconds);
        }
    }

    /// <summary>
    /// Auto batches track measured pages/sec and start small. An explicit
    /// <see cref="ClusterScheduleOptions.PagesPerBatch"/> stays fixed.
    /// </summary>
    private int AdaptiveBatch(string nodeId, int capacity)
    {
        int max = BatchTarget(capacity);
        if (_pagesPerBatch > 0)
            return max;
        if (!_pace.TryGetValue(nodeId, out Pace? pace) || pace.Pages <= 0 || pace.Seconds <= 0)
            return Math.Min(max, 2);
        double pps = pace.Pages / pace.Seconds;
        int sized = (int)Math.Round(pps * 8.0);
        if (sized < 1)
            sized = 1;
        return Math.Min(max, sized);
    }

    private int[] PickSpeculative(string nodeId, int want)
    {
        // Oldest primary lease first, pages this node is not already running, at most one extra copy.
        List<Lease> ordered = _leases.Values
            .Where(l => !l.Speculative)
            .OrderBy(l => l.Created)
            .ToList();
        List<int> picked = new(want);
        foreach (Lease lease in ordered)
        {
            if (lease.NodeId == nodeId)
                continue;
            foreach (int index in lease.Pages.OrderBy(i => i))
            {
                if (picked.Count >= want)
                    break;
                if (_pages[index].Done || picked.Contains(index))
                    continue;
                if (LeaseCount(index) >= 2)
                    continue;
                if (NodeHasPage(nodeId, index))
                    continue;
                picked.Add(index);
            }
        }

        return picked.ToArray();
    }

    private ClusterClaim LeasePages(string nodeId, int[] zeroBased, bool speculative, DateTimeOffset now)
    {
        int seq = ++_batchSeq;
        string batchId = "b" + seq.ToString("x");
        int leaseMs = Math.Clamp(
            Math.Max(_leaseFloorMs, zeroBased.Length * _pageTimeoutMs),
            _leaseFloorMs,
            _leaseCapMs);
        var lease = new Lease
        {
            BatchId = batchId,
            NodeId = nodeId,
            Pages = zeroBased.ToHashSet(),
            Deadline = now.AddMilliseconds(leaseMs),
            Speculative = speculative,
            Created = now,
        };
        _leases[batchId] = lease;
        SignalCore();
        int[] oneBased = new int[zeroBased.Length];
        for (int i = 0; i < zeroBased.Length; i++)
            oneBased[i] = zeroBased[i] + 1;
        return new ClusterClaim
        {
            Kind = ClusterClaimKind.Batch,
            BatchId = batchId,
            Pages = oneBased,
            LeaseMs = leaseMs,
            Speculative = speculative,
            Version = _version,
        };
    }

    private void ReapCore(DateTimeOffset now)
    {
        List<string> expired = [];
        foreach ((string id, Lease lease) in _leases)
        {
            if (lease.Deadline <= now)
                expired.Add(id);
        }

        foreach (string id in expired)
        {
            if (!_leases.TryGetValue(id, out Lease? lease))
                continue;
            string nodeId = lease.NodeId;
            bool speculative = lease.Speculative;
            int[] released = ReleaseCore(id);
            Array.Sort(released);
            _expiries.Add(new ClusterLeaseExpiry
            {
                BatchId = id,
                NodeId = nodeId,
                Pages = released,
                Speculative = speculative,
            });
        }
    }

    /// <summary>Lease-expiry events recorded by <see cref="Reap"/> and by <see cref="Claim"/>. Clears the buffer.</summary>
    public ClusterLeaseExpiry[] DrainExpiries()
    {
        lock (_gate)
        {
            if (_expiries.Count == 0)
                return [];
            ClusterLeaseExpiry[] copy = _expiries.ToArray();
            _expiries.Clear();
            return copy;
        }
    }

    private int[] ReleaseCore(string batchId)
    {
        if (!_leases.Remove(batchId, out Lease? lease))
            return [];
        List<int> back = [];
        foreach (int index in lease.Pages)
        {
            if (_pages[index].Done)
                continue;
            if (IsLeased(index))
                continue;
            if (_pages[index].Queued)
                continue;
            _pages[index].Queued = true;
            _pending.Enqueue(index);
            back.Add(index + 1);
        }

        return back.ToArray();
    }

    private void DetachPage(int index)
    {
        List<string> finished = [];
        foreach ((string id, Lease lease) in _leases)
        {
            if (!lease.Pages.Remove(index))
                continue;
            if (!lease.Pages.Any(p => !_pages[p].Done))
                finished.Add(id);
        }

        foreach (string id in finished)
            _leases.Remove(id);
    }

    private bool IsLeased(int index)
    {
        foreach (Lease lease in _leases.Values)
        {
            if (lease.Pages.Contains(index))
                return true;
        }

        return false;
    }

    private int LeaseCount(int index)
    {
        int n = 0;
        foreach (Lease lease in _leases.Values)
        {
            if (lease.Pages.Contains(index))
                n++;
        }

        return n;
    }

    private bool NodeHasPage(string nodeId, int index)
    {
        foreach (Lease lease in _leases.Values)
        {
            if (lease.NodeId == nodeId && lease.Pages.Contains(index))
                return true;
        }

        return false;
    }

    private int InFlightCore(string nodeId)
    {
        int n = 0;
        foreach (Lease lease in _leases.Values)
        {
            if (lease.NodeId != nodeId)
                continue;
            foreach (int index in lease.Pages)
            {
                if (!_pages[index].Done)
                    n++;
            }
        }

        return n;
    }

    private int CommittedCore(string nodeId) =>
        _committed.TryGetValue(nodeId, out int n) ? n : 0;

    private int CapacityOf(string nodeId)
    {
        if (_caps.TryGetValue(nodeId, out int cap) && cap > 0)
            return cap;
        return 1;
    }

    private int BatchTarget(int capacity)
    {
        if (_pagesPerBatch > 0)
            return Math.Clamp(_pagesPerBatch, 1, 64);
        return Math.Clamp(capacity, 1, 16);
    }

    private int Shrink(int want)
    {
        int pending = _pending.Count;
        if (pending <= 0)
            return want;
        int nodes = Math.Max(1, _expectedNodes);
        if (pending <= nodes)
            return 1;
        if (pending <= nodes * 2)
            return Math.Min(want, 2);
        return want;
    }

    private ClusterClaim WaitClaim() => new()
    {
        Kind = ClusterClaimKind.Wait,
        Version = _version,
        RetryAfterMs = 200,
    };

    private ClusterClaim DoneClaim() => new()
    {
        Kind = ClusterClaimKind.Done,
        Version = _version,
    };

    private void SignalCore()
    {
        _version++;
        if (_waiters.Count == 0)
            return;
        TaskCompletionSource<bool>[] copy = _waiters.ToArray();
        _waiters.Clear();
        foreach (TaskCompletionSource<bool> waiter in copy)
            waiter.TrySetResult(true);
    }

    private sealed class PageState
    {
        public bool Done;
        public bool Queued;
    }

    private sealed class Lease
    {
        public string BatchId { get; init; } = "";
        public string NodeId { get; init; } = "";
        public HashSet<int> Pages { get; init; } = [];
        public DateTimeOffset Deadline { get; init; }
        public bool Speculative { get; init; }
        public DateTimeOffset Created { get; init; }
    }

    private sealed class Pace
    {
        public int Pages;
        public double Seconds;
        public string? OpenBatch;
    }
}
