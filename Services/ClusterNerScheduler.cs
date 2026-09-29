using MiniOcr.Models;

namespace MiniOcr.Services;

public enum ClusterNerClaimKind
{
    Group = 0,
    Wait = 1,
    Done = 2,
}

/// <summary>One leased NER group. <see cref="PromptText"/> is the page block sent to the model; lookahead is separate.</summary>
public sealed class ClusterNerAssignment
{
    public ClusterNerClaimKind Kind { get; init; }
    public string GroupId { get; init; } = "";
    /// <summary>1-based page numbers in the group.</summary>
    public int[] Pages { get; init; } = [];
    public OcrPageResult[] Bodies { get; init; } = [];
    /// <summary>Exact group text (already split on <c>maxChars</c>). Does not include lookahead.</summary>
    public string PromptText { get; init; } = "";
    public string? Lookahead { get; init; }
    public int LookaheadPage { get; init; }
    public int LeaseMs { get; init; }
    public int RetryAfterMs { get; init; } = 200;
    public long Version { get; init; }
}

public sealed class ClusterNerOptions
{
    public int PageCount { get; init; }
    public int PagesPerRequest { get; init; } = 10;
    public int MaxChars { get; init; } = 300_000;
    public int LeaseMs { get; init; } = 135_000;
    public int MaxAttempts { get; init; } = 8;
    public string LocalNodeId { get; init; } = "local";
}

public sealed class ClusterNerSnapshot
{
    public int Done { get; init; }
    public int Formed { get; init; }
    public int InFlight { get; init; }
    public bool Sealed { get; init; }
    public bool Complete { get; init; }
    public List<ClusterNodeLoad> Nodes { get; init; } = [];
}

public readonly record struct ClusterNerGiveUp(string GroupId, int[] Pages, int Attempts);

/// <summary>
/// NER work queue for one PDF. Groups match <see cref="LlmPageGrouper"/>: consecutive
/// non-empty pages, split on <c>maxChars</c>, blank pages skipped. A group stays unleased
/// until the next non-empty page's first 240 characters are known (or OCR is sealed), so
/// the node that runs the group can join a name cut by the group boundary. Leases expire
/// and failed groups are retried by another LLM-capable node. Nodes with NER capacity 0
/// (no API key) are not given groups.
/// </summary>
public sealed class ClusterNerScheduler
{
    private readonly object _gate = new();
    private readonly int _pageCount;
    private readonly string _localNodeId;
    private readonly int _leaseMs;
    private readonly int _maxAttempts;
    private readonly LlmPageGrouper.OrderedBuffer _buffer;
    private readonly OcrPageResult?[] _accepted;
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly Queue<string> _ready = new();
    private readonly Dictionary<string, int> _caps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _completedBy = new(StringComparer.Ordinal);
    private readonly List<TaskCompletionSource<bool>> _waiters = [];
    private readonly List<ClusterLeaseExpiry> _expiries = [];
    private readonly List<ClusterNerGiveUp> _giveUps = [];
    private Group? _needsLookahead;
    private int _seq;
    private long _version;
    private bool _sealed;
    private bool _sawSuccess;

    public ClusterNerScheduler(ClusterNerOptions options)
    {
        if (options.PageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "PageCount must be positive.");
        _pageCount = options.PageCount;
        _localNodeId = string.IsNullOrWhiteSpace(options.LocalNodeId) ? "local" : options.LocalNodeId;
        _leaseMs = Math.Clamp(options.LeaseMs <= 0 ? 135_000 : options.LeaseMs, 1_000, 600_000);
        _maxAttempts = Math.Clamp(options.MaxAttempts <= 0 ? 8 : options.MaxAttempts, 1, 32);
        _buffer = new LlmPageGrouper.OrderedBuffer(
            options.PageCount,
            Math.Max(1, options.PagesPerRequest),
            Math.Max(1, options.MaxChars));
        _accepted = new OcrPageResult?[options.PageCount];
    }

    public string LocalNodeId => _localNodeId;

    public bool IsSealed
    {
        get { lock (_gate) return _sealed; }
    }

    public bool IsComplete
    {
        get { lock (_gate) return IsCompleteCore(); }
    }

    public bool SawSuccessfulExtract
    {
        get { lock (_gate) return _sawSuccess; }
    }

    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public bool LocalCanRun
    {
        get { lock (_gate) return _caps.GetValueOrDefault(_localNodeId) > 0; }
    }

    public bool HasAnyCapacity
    {
        get
        {
            lock (_gate)
                return _caps.Values.Any(c => c > 0);
        }
    }

    public void SetNerCapacity(string nodeId, int concurrency)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return;
        lock (_gate)
        {
            _caps[nodeId] = Math.Clamp(concurrency, 0, 32);
            SignalCore();
        }
    }

    public void AddPage(OcrPageResult page)
    {
        lock (_gate)
        {
            if (_sealed)
                return;
            int index = page.Page - 1;
            if ((uint)index >= (uint)_pageCount)
                return;
            if (_accepted[index] is not null)
                return;

            _accepted[index] = page;
            List<LlmPageGrouper.PageBatch> emitted;
            try
            {
                emitted = _buffer.Add(page);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            Group? previous = _needsLookahead;
            foreach (LlmPageGrouper.PageBatch batch in emitted)
            {
                Group group = CreateGroup(batch);
                previous = LinkLookahead(previous, group);
            }

            _needsLookahead = previous;
            AttachStoredLookahead();
            SignalCore();
        }
    }

    /// <summary>OCR is finished. The trailing partial group becomes claimable with no further lookahead.</summary>
    public void Seal()
    {
        lock (_gate)
        {
            if (_sealed)
                return;
            List<LlmPageGrouper.PageBatch> tail = _buffer.FlushRemainder();
            _sealed = true;
            Group? previous = _needsLookahead;
            foreach (LlmPageGrouper.PageBatch batch in tail)
                previous = LinkLookahead(previous, CreateGroup(batch));
            if (previous is not null && !previous.Queued && !previous.Done && previous.Owner is null)
                Enqueue(previous);
            _needsLookahead = null;
            SignalCore();
        }
    }

    public ClusterNerAssignment Claim(string nodeId, DateTimeOffset now)
    {
        lock (_gate)
        {
            ReapCore(now);
            if (IsCompleteCore())
                return DoneClaim();

            int cap = _caps.GetValueOrDefault(nodeId);
            if (cap <= 0 || string.IsNullOrWhiteSpace(nodeId))
                return WaitClaim();
            if (InFlightCore(nodeId) >= cap)
                return WaitClaim();

            bool deferred = false;
            int n = _ready.Count;
            for (int i = 0; i < n; i++)
            {
                string id = _ready.Dequeue();
                if (!_groups.TryGetValue(id, out Group? group) || group.Done || group.Owner is not null)
                {
                    if (group is not null)
                        group.Queued = false;
                    continue;
                }

                if (string.Equals(group.LastFailedBy, nodeId, StringComparison.Ordinal) && OthersCanTakeCore(nodeId))
                {
                    _ready.Enqueue(id);
                    deferred = true;
                    continue;
                }

                group.Queued = false;

                group.Owner = nodeId;
                group.Deadline = now.AddMilliseconds(_leaseMs);
                group.LastFailedBy = null;
                SignalCore();
                return ToAssignment(group, ClusterNerClaimKind.Group);
            }

            _ = deferred;
            return IsCompleteCore() ? DoneClaim() : WaitClaim();
        }
    }

    public bool TryComplete(
        string groupId,
        string nodeId,
        IEnumerable<string>? companies,
        IEnumerable<string>? persons,
        IReadOnlyList<ClusterNerEntityHit>? entities)
    {
        lock (_gate)
        {
            if (!_groups.TryGetValue(groupId, out Group? group))
                return false;
            if (group.Done)
                return string.Equals(group.CompletedBy, nodeId, StringComparison.Ordinal);
            if (!string.Equals(group.Owner, nodeId, StringComparison.Ordinal))
                return false;

            group.Companies.Clear();
            group.Persons.Clear();
            if (companies is not null)
            {
                foreach (string name in companies)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        group.Companies.Add(name);
                }
            }

            if (persons is not null)
            {
                foreach (string name in persons)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        group.Persons.Add(name);
                }
            }

            group.Entities = entities is null ? [] : entities.ToList();
            MarkDone(group, nodeId);
            _sawSuccess = true;
            _completedBy[nodeId] = _completedBy.GetValueOrDefault(nodeId) + 1;
            SignalCore();
            return true;
        }
    }

    public void Fail(string groupId, string nodeId)
    {
        lock (_gate)
        {
            if (!_groups.TryGetValue(groupId, out Group? group) || group.Done)
                return;
            if (!string.Equals(group.Owner, nodeId, StringComparison.Ordinal))
                return;
            group.Failures++;
            group.Owner = null;
            if (group.Failures >= _maxAttempts)
            {
                _giveUps.Add(new ClusterNerGiveUp(group.Id, group.Pages, group.Failures));
                MarkDone(group, "");
                SignalCore();
                return;
            }

            group.LastFailedBy = nodeId;
            Enqueue(group);
            SignalCore();
        }
    }

    public void DropNode(string nodeId, DateTimeOffset now)
    {
        lock (_gate)
        {
            List<string> held = [];
            foreach ((string id, Group group) in _groups)
            {
                if (!group.Done && string.Equals(group.Owner, nodeId, StringComparison.Ordinal))
                    held.Add(id);
            }

            foreach (string id in held)
                ReleaseCore(id, expired: false);
            _ = now;
            SignalCore();
        }
    }

    /// <summary>Return every remote lease to the queue so the local node can run it.</summary>
    public void TakeOverLocal(DateTimeOffset now)
    {
        lock (_gate)
        {
            List<string> held = [];
            foreach ((string id, Group group) in _groups)
            {
                if (group.Done || group.Owner is null)
                    continue;
                if (!string.Equals(group.Owner, _localNodeId, StringComparison.Ordinal))
                    held.Add(id);
            }

            foreach (string id in held)
                ReleaseCore(id, expired: false);
            _ = now;
            SignalCore();
        }
    }

    public void Reap(DateTimeOffset now)
    {
        lock (_gate)
        {
            ReapCore(now);
            if (_expiries.Count > 0)
                SignalCore();
        }
    }

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

    public ClusterNerGiveUp[] DrainGiveUps()
    {
        lock (_gate)
        {
            if (_giveUps.Count == 0)
                return [];
            ClusterNerGiveUp[] copy = _giveUps.ToArray();
            _giveUps.Clear();
            return copy;
        }
    }

    /// <summary>Mark every unfinished group done with no names so the job can finish.</summary>
    public int AbandonRemaining()
    {
        lock (_gate)
        {
            int abandoned = 0;
            foreach (Group group in _groups.Values)
            {
                if (group.Done)
                    continue;
                _giveUps.Add(new ClusterNerGiveUp(group.Id, group.Pages, group.Failures));
                group.Owner = null;
                MarkDone(group, "");
                abandoned++;
            }

            _ready.Clear();
            _sealed = true;
            SignalCore();
            return abandoned;
        }
    }

    public IReadOnlyList<ClusterNerEntityHit> EntitiesOf(string groupId)
    {
        lock (_gate)
        {
            if (!_groups.TryGetValue(groupId, out Group? group))
                return [];
            return group.Entities;
        }
    }

    public ClusterNerSnapshot Snapshot()
    {
        lock (_gate)
            return SnapshotCore();
    }

    public OcrEntities Merge()
    {
        lock (_gate)
        {
            List<OcrPageResult> pages = new(_pageCount);
            for (int i = 0; i < _pageCount; i++)
                pages.Add(_accepted[i] ?? new OcrPageResult { Page = i + 1, Text = "" });

            List<string> companies = [];
            List<string> persons = [];
            foreach (Group group in _groups.Values.OrderBy(g => g.Pages.Length == 0 ? int.MaxValue : g.Pages[0]))
            {
                companies.AddRange(group.Companies);
                persons.AddRange(group.Persons);
            }

            return EntityPostProcessor.Merge(pages, companies, persons);
        }
    }

    public Task WaitForChangeAsync(long seenVersion, TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            if (_version != seenVersion || IsCompleteCore())
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

    private bool IsCompleteCore() => _sealed && _groups.Values.All(g => g.Done);

    /// <summary>
    /// If <paramref name="previous"/> ends before <paramref name="next"/>, the head of
    /// <paramref name="next"/> is the cross-group lookahead and <paramref name="previous"/> becomes claimable.
    /// </summary>
    private Group LinkLookahead(Group? previous, Group next)
    {
        if (previous is not null &&
            !previous.Queued &&
            !previous.Done &&
            previous.Owner is null &&
            previous.Pages.Length > 0 &&
            next.Pages.Length > 0 &&
            previous.Pages[^1] < next.Pages[0] &&
            next.Bodies.Length > 0)
        {
            previous.Lookahead = ClusterNerPrompt.Head(next.Bodies[0].Text);
            previous.LookaheadPage = next.Bodies[0].Page;
            Enqueue(previous);
        }

        return next;
    }

    /// <summary>A page that arrived early may already be the lookahead for the group just sealed.</summary>
    private void AttachStoredLookahead()
    {
        if (_needsLookahead is null || _needsLookahead.Queued || _needsLookahead.Done || _needsLookahead.Owner is not null)
            return;
        if (_needsLookahead.Pages.Length == 0)
            return;
        int last = _needsLookahead.Pages[^1];
        for (int page = last + 1; page <= _pageCount; page++)
        {
            OcrPageResult? next = _accepted[page - 1];
            if (next is null)
                return;
            if (LlmPageGrouper.IsBlank(next))
                continue;
            _needsLookahead.Lookahead = ClusterNerPrompt.Head(next.Text);
            _needsLookahead.LookaheadPage = next.Page;
            Enqueue(_needsLookahead);
            _needsLookahead = null;
            return;
        }
    }

    private Group CreateGroup(LlmPageGrouper.PageBatch batch)
    {
        _seq++;
        var bodies = new OcrPageResult[batch.PageNumbers.Length];
        for (int i = 0; i < batch.PageNumbers.Length; i++)
        {
            int page = batch.PageNumbers[i];
            int index = page - 1;
            bodies[i] = (uint)index < (uint)_accepted.Length && _accepted[index] is not null
                ? _accepted[index]!
                : new OcrPageResult { Page = page, Text = "" };
        }

        var group = new Group
        {
            Id = "ner-" + _seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Pages = batch.PageNumbers,
            Bodies = bodies,
            PromptText = batch.Text,
        };
        _groups[group.Id] = group;
        return group;
    }

    private void Enqueue(Group group)
    {
        if (group.Done || group.Queued || group.Owner is not null)
            return;
        group.Queued = true;
        _ready.Enqueue(group.Id);
    }

    private void MarkDone(Group group, string nodeId)
    {
        group.Done = true;
        group.Queued = false;
        group.Owner = null;
        group.CompletedBy = nodeId;
    }

    private void ReleaseCore(string groupId, bool expired)
    {
        if (!_groups.TryGetValue(groupId, out Group? group) || group.Done)
            return;
        string owner = group.Owner ?? "";
        group.Owner = null;
        group.LastFailedBy = null;
        group.Queued = false;
        if (expired)
        {
            _expiries.Add(new ClusterLeaseExpiry
            {
                BatchId = group.Id,
                NodeId = owner,
                Pages = group.Pages,
                Speculative = false,
            });
        }

        Enqueue(group);
    }

    private void ReapCore(DateTimeOffset now)
    {
        List<string> expired = [];
        foreach ((string id, Group group) in _groups)
        {
            if (!group.Done && group.Owner is not null && group.Deadline <= now)
                expired.Add(id);
        }

        foreach (string id in expired)
            ReleaseCore(id, expired: true);
    }

    private int InFlightCore(string nodeId)
    {
        int n = 0;
        foreach (Group group in _groups.Values)
        {
            if (!group.Done && string.Equals(group.Owner, nodeId, StringComparison.Ordinal))
                n++;
        }

        return n;
    }

    private bool OthersCanTakeCore(string nodeId)
    {
        foreach ((string id, int cap) in _caps)
        {
            if (cap > 0 && !string.Equals(id, nodeId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private ClusterNerAssignment ToAssignment(Group group, ClusterNerClaimKind kind) => new()
    {
        Kind = kind,
        GroupId = group.Id,
        Pages = group.Pages,
        Bodies = group.Bodies,
        PromptText = group.PromptText,
        Lookahead = group.Lookahead,
        LookaheadPage = group.LookaheadPage,
        LeaseMs = _leaseMs,
        Version = _version,
    };

    private ClusterNerAssignment WaitClaim() => new()
    {
        Kind = ClusterNerClaimKind.Wait,
        RetryAfterMs = 200,
        Version = _version,
    };

    private ClusterNerAssignment DoneClaim() => new()
    {
        Kind = ClusterNerClaimKind.Done,
        Version = _version,
    };

    private ClusterNerSnapshot SnapshotCore()
    {
        var loads = new Dictionary<string, ClusterNodeLoad>(StringComparer.Ordinal);
        int inFlight = 0;
        foreach (Group group in _groups.Values)
        {
            if (!group.Done && group.Owner is not null)
            {
                inFlight++;
                Touch(loads, group.Owner).InFlight++;
            }
        }

        foreach ((string nodeId, int count) in _completedBy)
            Touch(loads, nodeId).PagesCommitted = count;

        return new ClusterNerSnapshot
        {
            Done = _groups.Values.Count(g => g.Done),
            Formed = _groups.Count,
            InFlight = inFlight,
            Sealed = _sealed,
            Complete = IsCompleteCore(),
            Nodes = loads.Values.OrderBy(n => n.NodeId, StringComparer.Ordinal).ToList(),
        };
    }

    private static ClusterNodeLoad Touch(Dictionary<string, ClusterNodeLoad> loads, string nodeId)
    {
        if (!loads.TryGetValue(nodeId, out ClusterNodeLoad? load))
        {
            load = new ClusterNodeLoad { NodeId = nodeId };
            loads[nodeId] = load;
        }

        return load;
    }

    private void SignalCore()
    {
        _version++;
        if (_waiters.Count == 0)
            return;
        foreach (TaskCompletionSource<bool> waiter in _waiters)
            waiter.TrySetResult(true);
        _waiters.Clear();
    }

    private sealed class Group
    {
        public string Id { get; init; } = "";
        public int[] Pages { get; init; } = [];
        public OcrPageResult[] Bodies { get; init; } = [];
        public string PromptText { get; init; } = "";
        public string? Lookahead { get; set; }
        public int LookaheadPage { get; set; }
        public bool Queued { get; set; }
        public bool Done { get; set; }
        public string? Owner { get; set; }
        public string CompletedBy { get; set; } = "";
        public string? LastFailedBy { get; set; }
        public DateTimeOffset Deadline { get; set; }
        public int Failures { get; set; }
        public List<string> Companies { get; } = [];
        public List<string> Persons { get; } = [];
        public List<ClusterNerEntityHit> Entities { get; set; } = [];
    }
}

/// <summary>Builds the distributed NER user message. The group text matches single-node grouping; the next-page head is context for a split name.</summary>
public static class ClusterNerPrompt
{
    public static string Head(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return text.Length <= EntityText.CrossPageLookaheadChars
            ? text
            : text[..EntityText.CrossPageLookaheadChars];
    }

    public static string WithLookahead(string groupText, string? lookahead, int lookaheadPage)
    {
        if (string.IsNullOrEmpty(lookahead))
            return groupText;
        return groupText +
            "--- 下一页开头（仅用于接上被页边界拆开的名字，不要单独收录只出现在这里的名字） page " +
            lookaheadPage.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " ---\n" +
            lookahead +
            "\n";
    }
}

/// <summary>
/// Turns one group's raw LLM names into protocol hits (page, count, originText).
/// The coordinator still re-merges every group's raw names against the full document.
/// </summary>
public static class ClusterNerAssembler
{
    public static List<ClusterNerEntityHit> Build(
        IReadOnlyList<OcrPageResult> groupPages,
        string? lookahead,
        LlmEntityPayload payload)
    {
        List<string> companies = payload.Companies ?? [];
        List<string> persons = payload.Persons ?? [];
        if (groupPages.Count == 0)
            return [];

        List<OcrPageResult> scan = new(groupPages.Count + 1);
        scan.AddRange(groupPages);
        if (!string.IsNullOrEmpty(lookahead))
            scan.Add(new OcrPageResult { Page = 0, Text = lookahead });

        OcrEntities merged = EntityPostProcessor.Merge(scan, companies, persons);
        List<string> companyNames = merged.Companies.Select(c => c.Name).Where(n => n.Length > 0).ToList();
        List<string> personNames = merged.Persons.Select(p => p.Name).Where(n => n.Length > 0).ToList();
        if (companyNames.Count == 0 && personNames.Count == 0)
            return [];

        ChallengeFileResult file = ChallengeResultMapper.BuildFileResult("ner", scan, companyNames, personNames);
        List<ClusterNerEntityHit> hits = [];
        foreach (ChallengePageResult page in file.Pages)
        {
            if (page.Page <= 0)
                continue;
            foreach (ChallengeRule rule in page.RuleList)
            {
                string kind = rule.RuleCode switch
                {
                    "B04" => "person",
                    "B06" => "company",
                    _ => "",
                };
                if (kind.Length == 0)
                    continue;
                foreach (ChallengeRuleItem item in rule.RuleItemList)
                {
                    string name = kind == "person" ? item.PersonName ?? "" : item.CompanyName ?? "";
                    if (name.Length == 0 || item.OriginText.Count == 0)
                        continue;
                    hits.Add(new ClusterNerEntityHit
                    {
                        Kind = kind,
                        Name = name,
                        Page = page.Page,
                        Count = item.Count,
                        OriginText = item.OriginText,
                    });
                }
            }
        }

        return hits;
    }
}
