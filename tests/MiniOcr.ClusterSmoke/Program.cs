using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MiniOcr.Models;
using MiniOcr.Services;

int failed = 0;

void AssertTrue(bool cond, string msg)
{
    if (cond)
    {
        Console.WriteLine("  PASS  " + msg);
        return;
    }

    Console.WriteLine("  FAIL  " + msg);
    failed++;
}

ClusterPageScheduler NewScheduler(
    int pages,
    int expectedNodes = 1,
    int pagesPerBatch = 0,
    int tail = 4,
    int leaseFloorMs = 1_000,
    int pageTimeoutMs = 1_000)
{
    return new ClusterPageScheduler(new ClusterScheduleOptions
    {
        PageCount = pages,
        LocalNodeId = "local",
        PagesPerBatch = pagesPerBatch,
        LeaseFloorMs = leaseFloorMs,
        PageTimeoutMs = pageTimeoutMs,
        LeaseCapMs = 60_000,
        SpeculativeTailPages = tail,
        ExpectedNodes = expectedNodes,
    });
}

void CommitAll(ClusterPageScheduler sched, ClusterClaim claim)
{
    foreach (int page in claim.Pages)
        AssertTrue(sched.TryCommit(claim.BatchId, page), $"commit page {page} batch {claim.BatchId}");
}

Console.WriteLine("=== config defaults off ===");
ClusterRuntimeConfig off = ClusterRuntimeConfig.Resolve(new AppConfigFile(), _ => null);
AssertTrue(!off.Enabled, "missing cluster section is off");
AssertTrue(!off.IsWorker || off.Role == "coordinator", "default role is coordinator");
AssertTrue(off.Role == "coordinator", "role coordinator");

Console.WriteLine("=== empty token forces off ===");
ClusterRuntimeConfig noToken = ClusterRuntimeConfig.Resolve(
    new AppConfigFile
    {
        Cluster = new ClusterFileConfig { Enabled = true, Token = "  " },
    },
    _ => null);
AssertTrue(!noToken.Enabled, "blank token disables cluster");
AssertTrue(noToken.DisabledReason is not null, "disabled reason set");

Console.WriteLine("=== env overrides file ===");
var env = new Dictionary<string, string?>(StringComparer.Ordinal)
{
    ["MINIOCR_CLUSTER_ENABLED"] = "1",
    ["MINIOCR_CLUSTER_ROLE"] = "worker",
    ["MINIOCR_CLUSTER_TOKEN"] = "s3cret",
    ["MINIOCR_CLUSTER_NODE_ID"] = "mac-1",
    ["MINIOCR_CLUSTER_WORKERS"] = "http://10.0.0.2:5081/, http://10.0.0.3:5082",
    ["MINIOCR_CLUSTER_CAPACITY"] = "3",
    ["MINIOCR_CLUSTER_PAGES_PER_BATCH"] = "5",
    ["MINIOCR_CLUSTER_JOIN_GRACE_MS"] = "2500",
};
ClusterRuntimeConfig on = ClusterRuntimeConfig.Resolve(
    new AppConfigFile
    {
        Cluster = new ClusterFileConfig
        {
            Enabled = false,
            Role = "coordinator",
            Token = "file-token",
            Workers = [new ClusterWorkerFileConfig { Url = "http://ignored", Capacity = 9 }],
        },
    },
    name => env.TryGetValue(name, out string? v) ? v : null);
AssertTrue(on.Enabled && on.IsWorker && !on.IsCoordinator, "env role worker");
AssertTrue(on.Token == "s3cret" && on.NodeId == "mac-1", "env token and node id");
AssertTrue(on.Workers.Count == 2 && on.Workers[0].Url == "http://10.0.0.2:5081", "env worker list trimmed");
AssertTrue(on.Capacity == 3 && on.PagesPerBatch == 5 && on.JoinGraceMs == 2500, "numeric env overrides");
AssertTrue(!on.VerboseDispatch, "verbose dispatch stays off when unset");
AssertTrue(on.EffectiveCapacity(8, 32, llmMode: false) == 3, "explicit capacity wins over engines");
AssertTrue(
    ClusterRuntimeConfig.Resolve(new AppConfigFile(), _ => null).EffectiveCapacity(0, 32, llmMode: true) == 32,
    "llm capacity falls back to vision concurrency");

Console.WriteLine("=== auth ===");
AssertTrue(ClusterAuth.FixedEquals("s3cret", "s3cret"), "matching token");
AssertTrue(!ClusterAuth.FixedEquals("s3cret", "s3creT"), "case sensitive");
AssertTrue(!ClusterAuth.FixedEquals("", "s3cret"), "empty provided");
AssertTrue(!ClusterAuth.FixedEquals("s3cret", ""), "empty expected");
AssertTrue(!ClusterAuth.FixedEquals(null, "x"), "null provided");

Console.WriteLine("=== pull balance: larger capacity takes more pages ===");
{
    ClusterPageScheduler sched = NewScheduler(20, expectedNodes: 2);
    sched.SetCapacity("slow", 1);
    sched.SetCapacity("fast", 4);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    int guard = 0;
    while (!sched.IsComplete && guard++ < 100)
    {
        ClusterClaim slow = sched.Claim("slow", 8, now);
        if (slow.Kind == ClusterClaimKind.Batch)
            CommitAll(sched, slow);
        ClusterClaim fast = sched.Claim("fast", 8, now);
        if (fast.Kind == ClusterClaimKind.Batch)
            CommitAll(sched, fast);
        if (slow.Kind != ClusterClaimKind.Batch && fast.Kind != ClusterClaimKind.Batch)
            break;
    }

    ClusterScheduleSnapshot snap = sched.Snapshot();
    int slowPages = snap.Nodes.FirstOrDefault(n => n.NodeId == "slow")?.PagesCommitted ?? 0;
    int fastPages = snap.Nodes.FirstOrDefault(n => n.NodeId == "fast")?.PagesCommitted ?? 0;
    AssertTrue(sched.IsComplete, "fairness run completed");
    AssertTrue(slowPages + fastPages == 20, $"all 20 pages attributed ({slowPages}+{fastPages})");
    AssertTrue(fastPages > slowPages, $"fast node got more pages (fast={fastPages} slow={slowPages})");
}

Console.WriteLine("=== in-flight cap and tail shrink ===");
{
    ClusterPageScheduler sched = NewScheduler(6, expectedNodes: 6, pagesPerBatch: 8);
    sched.SetCapacity("a", 2);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim first = sched.Claim("a", 8, now);
    AssertTrue(first.Kind == ClusterClaimKind.Batch && first.Pages.Length == 1, "tail shrink hands out 1 page when pending <= nodes");
    ClusterClaim second = sched.Claim("a", 8, now);
    AssertTrue(second.Kind == ClusterClaimKind.Batch && second.Pages.Length == 1, "second page while room remains");
    ClusterClaim third = sched.Claim("a", 8, now);
    AssertTrue(third.Kind == ClusterClaimKind.Wait, "in-flight cap blocks a third page");
    AssertTrue(!sched.TryCommit("nope", first.Pages[0]), "unknown batch rejected");
    AssertTrue(sched.TryCommit(first.BatchId, first.Pages[0]), "first commit");
    AssertTrue(!sched.TryCommit(first.BatchId, first.Pages[0]), "duplicate commit ignored");
}

Console.WriteLine("=== expired lease is retried by another node ===");
{
    DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    ClusterPageScheduler sched = NewScheduler(3, expectedNodes: 1, pagesPerBatch: 3, tail: 1, leaseFloorMs: 5_000, pageTimeoutMs: 1_000);
    sched.SetCapacity("dead", 3);
    sched.SetCapacity("live", 3);
    ClusterClaim leased = sched.Claim("dead", 3, now);
    AssertTrue(leased.Kind == ClusterClaimKind.Batch && leased.Pages.Length == 3, "dead node took 3 pages");
    ClusterClaim blocked = sched.Claim("live", 3, now.AddSeconds(1));
    AssertTrue(blocked.Kind == ClusterClaimKind.Wait, "live node waits while lease holds");
    ClusterClaim retry = sched.Claim("live", 3, now.AddMilliseconds(leased.LeaseMs + 1));
    AssertTrue(retry.Kind == ClusterClaimKind.Batch, "after expiry the other node gets the pages");
    AssertTrue(retry.Pages.OrderBy(p => p).SequenceEqual(leased.Pages.OrderBy(p => p)), "same pages retried");
    ClusterLeaseExpiry[] expired = sched.DrainExpiries();
    AssertTrue(expired.Length == 1 && expired[0].NodeId == "dead", "expiry recorded for the dead lease");
    AssertTrue(expired[0].Pages.OrderBy(p => p).SequenceEqual(leased.Pages.OrderBy(p => p)), "requeued pages match the expired lease");
    AssertTrue(sched.DrainExpiries().Length == 0, "expiry buffer is cleared");
    CommitAll(sched, retry);
    AssertTrue(sched.IsComplete, "job completes on the retry node");
    AssertTrue(!sched.TryCommit(leased.BatchId, leased.Pages[0]), "late owner cannot overwrite");
    ClusterScheduleSnapshot snap = sched.Snapshot();
    int livePages = snap.Nodes.First(n => n.NodeId == "live").PagesCommitted;
    int deadPages = snap.Nodes.FirstOrDefault(n => n.NodeId == "dead")?.PagesCommitted ?? 0;
    AssertTrue(livePages == 3 && deadPages == 0, "only the node that finished is credited");
}

Console.WriteLine("=== drop node requeues immediately ===");
{
    ClusterPageScheduler sched = NewScheduler(2, pagesPerBatch: 2);
    sched.SetCapacity("gone", 2);
    sched.SetCapacity("local", 2);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim claim = sched.Claim("gone", 2, now);
    AssertTrue(claim.Pages.Length == 2, "node took both pages");
    sched.DropNode("gone", now);
    ClusterClaim local = sched.Claim("local", 2, now);
    AssertTrue(local.Kind == ClusterClaimKind.Batch && local.Pages.Length == 2, "local picked up dropped pages");
    CommitAll(sched, local);
    AssertTrue(sched.IsComplete, "complete after drop");
}

Console.WriteLine("=== speculative tail ===");
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterPageScheduler sched = NewScheduler(2, expectedNodes: 1, pagesPerBatch: 2, tail: 2, leaseFloorMs: 30_000);
    sched.SetCapacity("slow", 2);
    sched.SetCapacity("idle", 2);
    ClusterClaim slow = sched.Claim("slow", 2, now);
    AssertTrue(slow.Kind == ClusterClaimKind.Batch && slow.Pages.Length == 2, "slow node holds the tail");
    ClusterClaim copy = sched.Claim("idle", 2, now);
    AssertTrue(copy.Kind == ClusterClaimKind.Batch && copy.Speculative, "idle node speculatively copies the tail");
    AssertTrue(copy.Pages.OrderBy(p => p).SequenceEqual(slow.Pages.OrderBy(p => p)), "speculative pages match");
    CommitAll(sched, copy);
    AssertTrue(sched.IsComplete, "speculative finish completes the job");
    foreach (int page in slow.Pages)
        AssertTrue(!sched.TryCommit(slow.BatchId, page), $"original lease page {page} lost the race");
    ClusterScheduleSnapshot snap = sched.Snapshot();
    AssertTrue(snap.Nodes.First(n => n.NodeId == "idle").PagesCommitted == 2, "idle node credited");
    AssertTrue((snap.Nodes.FirstOrDefault(n => n.NodeId == "slow")?.PagesCommitted ?? 0) == 0, "slow node not credited");
}

Console.WriteLine("=== local window hold leaves pages for remotes ===");
{
    ClusterPageScheduler sched = NewScheduler(5, expectedNodes: 3, pagesPerBatch: 4);
    sched.SetCapacity("local", 1);
    sched.SetCapacity("w", 1);
    sched.SetHoldLocalWindow(true);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim local = sched.Claim("local", 4, now);
    AssertTrue(local.Kind == ClusterClaimKind.Batch && local.Pages.Length == 1, "local takes one page");
    CommitAll(sched, local);
    ClusterClaim held = sched.Claim("local", 4, now);
    AssertTrue(held.Kind == ClusterClaimKind.Wait, "local held at its window");
    ClusterClaim remote = sched.Claim("w", 4, now);
    AssertTrue(remote.Kind == ClusterClaimKind.Batch, "remote still receives pages during the hold");
    sched.SetHoldLocalWindow(false);
    ClusterClaim again = sched.Claim("local", 4, now);
    AssertTrue(again.Kind == ClusterClaimKind.Batch, "clearing the hold lets local continue");
}

Console.WriteLine("=== takeover drops only remote leases ===");
{
    ClusterPageScheduler sched = NewScheduler(4, pagesPerBatch: 2);
    sched.SetCapacity("local", 2);
    sched.SetCapacity("remote", 2);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim local = sched.Claim("local", 2, now);
    ClusterClaim remote = sched.Claim("remote", 2, now);
    AssertTrue(local.Kind == ClusterClaimKind.Batch && remote.Kind == ClusterClaimKind.Batch, "both nodes leased work");
    sched.TakeOverLocal(now);
    AssertTrue(sched.TryCommit(local.BatchId, local.Pages[0]), "local lease survived takeover");
    ClusterClaim rest = sched.Claim("local", 4, now);
    AssertTrue(rest.Kind == ClusterClaimKind.Batch, "remote pages came back to local");
}

Console.WriteLine("=== model mismatch warning ===");
AssertTrue(
    ClusterNodeRegistry.Mismatch("llm", "gpt-4o", 72, "local", "ChineseV6Tiny", 96) is not null,
    "mode, model, and dpi differences are reported");
AssertTrue(
    ClusterNodeRegistry.Mismatch("local", "ChineseV6Tiny", 96, "local", "ChineseV6Tiny", 96) is null,
    "identical nodes produce no warning");

Console.WriteLine("=== NER groups stay in document order when OCR finishes out of order ===");
{
    var buffer = new LlmPageGrouper.OrderedBuffer(12, pagesPerRequest: 10, maxChars: 100_000);
    List<LlmPageGrouper.PageBatch> emitted = [];
    int[] order = [3, 1, 5, 2, 4, 8, 6, 10, 7, 9, 12, 11];
    foreach (int page in order)
    {
        emitted.AddRange(buffer.Add(new OcrPageResult
        {
            Page = page,
            Text = "page-" + page,
        }));
    }

    AssertTrue(emitted.Count == 1, "one group emitted once pages 1..10 exist");
    AssertTrue(
        emitted[0].PageNumbers.SequenceEqual(Enumerable.Range(1, 10)),
        "group page numbers are 1..10 in order");
    List<LlmPageGrouper.PageBatch> tail = buffer.FlushRemainder();
    AssertTrue(tail.Count == 1 && tail[0].PageNumbers.SequenceEqual([11, 12]), "tail keeps 11 then 12");
    AssertTrue(!emitted[0].Text.Contains("--- page 11 ---", StringComparison.Ordinal), "later pages are not pulled forward");
}

Console.WriteLine("=== verboseDispatch config ===");
{
    ClusterRuntimeConfig fromEnv = ClusterRuntimeConfig.Resolve(
        new AppConfigFile
        {
            Cluster = new ClusterFileConfig { Enabled = true, Token = "t", VerboseDispatch = false },
        },
        name => name == "MINIOCR_CLUSTER_VERBOSE_DISPATCH" ? "1" : null);
    AssertTrue(fromEnv.VerboseDispatch, "MINIOCR_CLUSTER_VERBOSE_DISPATCH=1");
    ClusterRuntimeConfig fromFile = ClusterRuntimeConfig.Resolve(
        new AppConfigFile { Cluster = new ClusterFileConfig { VerboseDispatch = true } },
        _ => null);
    AssertTrue(fromFile.VerboseDispatch, "file verboseDispatch true");
    ClusterRuntimeConfig envOff = ClusterRuntimeConfig.Resolve(
        new AppConfigFile { Cluster = new ClusterFileConfig { VerboseDispatch = true } },
        name => name == "MINIOCR_CLUSTER_VERBOSE_DISPATCH" ? "off" : null);
    AssertTrue(!envOff.VerboseDispatch, "env off overrides file");
}

Console.WriteLine("=== dispatch log level (before/after at Information) ===");
{
    const string jobId = "job-demo";
    const int total = 24;
    ClusterPageScheduler sched = NewScheduler(total, expectedNodes: 2, pagesPerBatch: 2, tail: 4, leaseFloorMs: 20_000);
    sched.SetCapacity("coord", 2);
    sched.SetCapacity("worker-a", 2);
    DateTimeOffset started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    DateTimeOffset now = started;
    var before = new List<string>();
    var afterLines = new List<(LogLevel Level, string Text)>();
    var after = new MemoryLogger(afterLines, LogLevel.Information);
    int lastDone = 0;
    int lastBucket = 0;
    DateTimeOffset lastAt = started;
    string[] nodes = ["coord", "worker-a"];
    int turn = 0;
    int guard = 0;
    while (!sched.IsComplete && guard++ < 80)
    {
        string node = nodes[turn++ % nodes.Length];
        ClusterClaim claim = sched.Claim(node, 2, now);
        if (claim.Kind != ClusterClaimKind.Batch)
            break;
        string pages = string.Join(",", claim.Pages);
        before.Add(
            "info: Cluster job " + jobId + " claim node=" + node + " batch=" + claim.BatchId +
            " pages=" + pages + " speculative=" + claim.Speculative + " leaseMs=" + claim.LeaseMs);
        before.Add(
            "info: Cluster worker " + node + " job " + jobId + " batch " + claim.BatchId +
            " done pages=" + claim.Pages.Length);
        ClusterJobLog.Claim(after, verbose: false, jobId, node, claim.BatchId, pages, claim.Speculative, claim.LeaseMs);
        ClusterJobLog.BatchDone(after, verbose: false, node, jobId, claim.BatchId, claim.Pages.Length);
        ClusterJobLog.Heartbeat(after, verbose: false, node, 0, 2);
        ClusterJobLog.DispatchWait(after, verbose: false, node);
        foreach (int page in claim.Pages)
            sched.TryCommit(claim.BatchId, page);
        ClusterScheduleSnapshot snap = sched.Snapshot();
        ClusterJobLog.ProgressDecision decision = ClusterJobLog.EvaluateProgress(
            snap.Done, total, started, lastDone, lastBucket, lastAt, now);
        if (decision.Log)
        {
            ClusterJobLog.Progress(
                after, jobId, snap.Done, total, decision.Percent, decision.PagesPerSecond,
                snap.LeasedPages, ClusterJobLog.FormatByNode(snap));
            lastDone = snap.Done;
            lastBucket = decision.Bucket;
            lastAt = now;
        }

        now = now.AddSeconds(1);
    }

    ClusterScheduleSnapshot finalSnap = sched.Snapshot();
    double elapsedMs = (now - started).TotalMilliseconds;
    after.Log(
        LogLevel.Information,
        new EventId(0),
        "Cluster job " + jobId + " done: pages=" + total + " elapsedMs=" + elapsedMs.ToString("F0") +
        " " + (total / Math.Max(0.001, elapsedMs / 1000.0)).ToString("F1") + " pages/s byNode=" +
        ClusterJobLog.FormatByNode(finalSnap),
        null,
        static (state, _) => state);

    DateTimeOffset t0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    var early = ClusterJobLog.EvaluateProgress(1, 100, t0, 0, 0, t0, t0.AddMilliseconds(200));
    AssertTrue(!early.Log, "progress min gap holds");
    var step = ClusterJobLog.EvaluateProgress(10, 100, t0, 0, 0, t0, t0.AddSeconds(1));
    AssertTrue(step.Log && step.Percent == 10, "10% step logs after the min gap");
    var finished = ClusterJobLog.EvaluateProgress(100, 100, t0, 90, 9, t0, t0.AddSeconds(5));
    AssertTrue(!finished.Log, "100% is the final byNode line, not another progress line");

    AssertTrue(sched.IsComplete, "demo job completed");
    AssertTrue(before.Count >= 12, "before sample is a per-claim flood (" + before.Count + " lines)");
    int infoAfter = afterLines.Count(l => l.Level == LogLevel.Information);
    AssertTrue(infoAfter > 0 && infoAfter < before.Count, "after Information lines are fewer (" + infoAfter + " < " + before.Count + ")");
    AssertTrue(
        !afterLines.Any(l => l.Text.Contains(" claim node=", StringComparison.Ordinal)),
        "default Information has no per-claim line");
    AssertTrue(
        !afterLines.Any(l => l.Text.Contains("done pages=", StringComparison.Ordinal)),
        "default Information has no per-batch done line");
    AssertTrue(afterLines.Any(l => l.Text.Contains("progress:", StringComparison.Ordinal)), "progress summary is Information");
    AssertTrue(afterLines.Any(l => l.Text.Contains("byNode=", StringComparison.Ordinal)), "final breakdown stays Information");
    AssertTrue(!afterLines.Any(l => l.Text.Contains("heartbeat", StringComparison.Ordinal)), "heartbeat hidden at Information");
    AssertTrue(!afterLines.Any(l => l.Text.Contains("dispatch poll", StringComparison.Ordinal)), "empty poll hidden at Information");

    ClusterPageScheduler tail = NewScheduler(2, expectedNodes: 1, pagesPerBatch: 2, tail: 2, leaseFloorMs: 30_000);
    tail.SetCapacity("slow", 2);
    tail.SetCapacity("idle", 2);
    _ = tail.Claim("slow", 2, t0);
    ClusterClaim copy = tail.Claim("idle", 2, t0);
    AssertTrue(copy.Speculative, "demo speculative claim");
    ClusterJobLog.Claim(after, verbose: false, jobId, "idle", copy.BatchId, string.Join(",", copy.Pages), copy.Speculative, copy.LeaseMs);

    ClusterPageScheduler dead = NewScheduler(2, pagesPerBatch: 2, leaseFloorMs: 1_000, pageTimeoutMs: 1_000);
    dead.SetCapacity("dead", 2);
    dead.SetCapacity("live", 2);
    ClusterClaim held = dead.Claim("dead", 2, t0);
    _ = dead.Claim("live", 2, t0.AddMilliseconds(held.LeaseMs + 1));
    foreach (ClusterLeaseExpiry exp in dead.DrainExpiries())
    {
        ClusterJobLog.LeaseExpired(
            after,
            jobId,
            exp.NodeId,
            exp.BatchId,
            exp.Pages.Length == 0 ? "(none)" : string.Join(",", exp.Pages),
            exp.Speculative);
    }

    AssertTrue(
        afterLines.Any(l => l.Level == LogLevel.Information && l.Text.Contains("speculative retry", StringComparison.Ordinal)),
        "speculative retry stays Information");
    AssertTrue(
        afterLines.Any(l => l.Level == LogLevel.Information && l.Text.Contains("lease expired", StringComparison.Ordinal)),
        "lease expiry stays Information");

    var verboseLines = new List<(LogLevel Level, string Text)>();
    var verbose = new MemoryLogger(verboseLines, LogLevel.Information);
    ClusterJobLog.Claim(verbose, verbose: true, jobId, "coord", "b1", "1,2", speculative: false, leaseMs: 20_000);
    ClusterJobLog.Heartbeat(verbose, verbose: true, "coord", 2, 2);
    ClusterJobLog.DispatchWait(verbose, verbose: true, "worker-a");
    ClusterJobLog.BatchDone(verbose, verbose: true, "worker-a", jobId, "b1", 2);
    AssertTrue(
        verboseLines.Count == 4 && verboseLines.All(l => l.Level == LogLevel.Information),
        "verboseDispatch promotes routine lines to Information");

    var debugLines = new List<(LogLevel Level, string Text)>();
    var debug = new MemoryLogger(debugLines, LogLevel.Debug);
    ClusterJobLog.Claim(debug, verbose: false, jobId, "coord", "b1", "1,2", speculative: false, leaseMs: 20_000);
    ClusterJobLog.Heartbeat(debug, verbose: false, "coord", 0, 2);
    ClusterJobLog.DispatchWait(debug, verbose: false, "worker-a");
    AssertTrue(
        debugLines.Count == 3 && debugLines.All(l => l.Level == LogLevel.Debug),
        "Logging:LogLevel Debug shows routine dispatch lines");

    Console.WriteLine("--- before: default Information (previous per-claim / per-batch lines) ---");
    foreach (string line in before)
        Console.WriteLine(line);
    Console.WriteLine("--- after: default Information ---");
    foreach ((LogLevel level, string text) in afterLines)
    {
        if (level >= LogLevel.Information)
            Console.WriteLine(level.ToString().ToLowerInvariant() + ": " + text);
    }

    Console.WriteLine("--- suppressed at Information: " + before.Count + " routine lines; progress, speculative retry, and lease expiry kept ---");
}

Console.WriteLine("=== distributed NER config ===");
{
    ClusterRuntimeConfig defaults = ClusterRuntimeConfig.Resolve(
        new AppConfigFile { Cluster = new ClusterFileConfig { Enabled = true, Token = "t" } },
        _ => null);
    AssertTrue(defaults.DistributedNer, "distributed NER defaults on when unset");
    ClusterRuntimeConfig fileOff = ClusterRuntimeConfig.Resolve(
        new AppConfigFile { Cluster = new ClusterFileConfig { Enabled = true, Token = "t", DistributedNer = false } },
        _ => null);
    AssertTrue(!fileOff.DistributedNer, "file distributedNer false");
    ClusterRuntimeConfig envOn = ClusterRuntimeConfig.Resolve(
        new AppConfigFile { Cluster = new ClusterFileConfig { Enabled = true, Token = "t", DistributedNer = false } },
        name => name == "MINIOCR_CLUSTER_DISTRIBUTED_NER" ? "1" : null);
    AssertTrue(envOn.DistributedNer, "MINIOCR_CLUSTER_DISTRIBUTED_NER=1 overrides file");
    ClusterRuntimeConfig envOff = ClusterRuntimeConfig.Resolve(
        new AppConfigFile { Cluster = new ClusterFileConfig { Enabled = true, Token = "t" } },
        name => name == "MINIOCR_CLUSTER_DISTRIBUTED_NER" ? "off" : null);
    AssertTrue(!envOff.DistributedNer, "MINIOCR_CLUSTER_DISTRIBUTED_NER=off");

    var self = new ClusterSelf
    {
        NodeId = "coord",
        Role = "coordinator",
        Capacity = 2,
        EngineCount = 2,
        OcrMode = "local",
        Model = "ChineseV6Tiny",
        Dpi = 96,
        AdvertiseUrl = "http://127.0.0.1:5080",
        LlmConfigured = true,
        NerConcurrency = 8,
    };
    ClusterHealthInfo health = new ClusterNodeRegistry(self, 3_000).BuildHealth([], null);
    ClusterNodeHealth local = health.Nodes.Single(n => n.Local);
    AssertTrue(local.LlmConfigured && local.NerConcurrency == 8, "health reports that the local node has an LLM key");
}

Console.WriteLine("=== distributed NER groups, lookahead, failure, merge ===");
{
    string page2 = "腾科技有限公司法定代表人张伟签署本合同正文内容补充说明" + new string('。', 280);
    string page4 = "乙方北京华腾科技有限公司再次出现，联系人张伟确认条款有效并签字。";
    OcrPageResult[] doc =
    [
        new() { Page = 1, Text = "甲方北京华" },
        new() { Page = 2, Text = page2 },
        new() { Page = 3, Text = "   " },
        new() { Page = 4, Text = page4 },
    ];

    ClusterNerScheduler sched = new(new ClusterNerOptions
    {
        PageCount = 4,
        PagesPerRequest = 1,
        MaxChars = 100_000,
        LeaseMs = 5_000,
        MaxAttempts = 4,
        LocalNodeId = "coord",
    });
    sched.SetNerCapacity("worker-a", 1);
    sched.SetNerCapacity("worker-b", 2);
    sched.SetNerCapacity("no-key", 0);
    foreach (OcrPageResult page in doc)
        sched.AddPage(page);

    DateTimeOffset now = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    ClusterNerAssignment blocked = sched.Claim("no-key", now);
    AssertTrue(blocked.Kind == ClusterNerClaimKind.Wait, "node without an LLM key is not given a NER group");

    ClusterNerAssignment first = sched.Claim("worker-a", now);
    AssertTrue(first.Kind == ClusterNerClaimKind.Group && first.Pages.SequenceEqual([1]), "first group is page 1");
    AssertTrue(first.LookaheadPage == 2, "page 1 group carries the next page number");
    AssertTrue(first.Lookahead is not null && first.Lookahead.Length == 240, "lookahead is the 240-char head");
    AssertTrue(first.Lookahead!.Contains("腾科技有限公司", StringComparison.Ordinal), "lookahead includes the split company tail");
    AssertTrue(!first.Lookahead.Contains(page4, StringComparison.Ordinal), "lookahead is not the whole following page");
    string prompt = ClusterNerPrompt.WithLookahead(first.PromptText, first.Lookahead, first.LookaheadPage);
    AssertTrue(prompt.Contains("下一页开头", StringComparison.Ordinal), "prompt marks the next-page head as context");
    AssertTrue(prompt.Contains("甲方北京华", StringComparison.Ordinal) && prompt.Contains("腾科技有限公司", StringComparison.Ordinal),
        "owning node sees both halves of a cross-group name");

    LlmEntityPayload Fake(ClusterNerAssignment claim)
    {
        string text = ClusterNerPrompt.WithLookahead(claim.PromptText, claim.Lookahead, claim.LookaheadPage);
        List<string> companies = [];
        List<string> persons = [];
        if (text.Contains("北京华", StringComparison.Ordinal) && text.Contains("腾科技有限公司", StringComparison.Ordinal))
            companies.Add("北京华腾科技有限公司");
        else if (text.Contains("北京华腾科技有限公司", StringComparison.Ordinal))
            companies.Add("北京华腾科技有限公司");
        if (text.Contains("张伟", StringComparison.Ordinal))
            persons.Add("张伟");
        return new LlmEntityPayload { Companies = companies, Persons = persons };
    }

    bool CommitFake(ClusterNerAssignment claim, string nodeId)
    {
        LlmEntityPayload payload = Fake(claim);
        return sched.TryComplete(
            claim.GroupId,
            nodeId,
            payload.Companies,
            payload.Persons,
            ClusterNerAssembler.Build(claim.Bodies, claim.Lookahead, payload));
    }

    List<ClusterNerEntityHit> firstHits = ClusterNerAssembler.Build(first.Bodies, first.Lookahead, Fake(first));
    ClusterNerEntityHit? boundary = firstHits.FirstOrDefault(h => h.Kind == "company" && h.Page == 1);
    AssertTrue(boundary is not null, "worker returns a company hit on the boundary page");
    AssertTrue(boundary!.OriginText.Count > 0 && boundary.OriginText.All(t => t.Length is >= 10 and <= 100),
        "originText is 10–100 chars");
    AssertTrue(boundary.OriginText.All(t => t.Contains("北京华腾科技有限公司", StringComparison.Ordinal)),
        "originText contains the joined company name");
    AssertTrue(CommitFake(first, "worker-a"), "page 1 group commits");

    ClusterNerAssignment second = sched.Claim("worker-b", now);
    AssertTrue(second.Kind == ClusterNerClaimKind.Group && second.Pages.SequenceEqual([2]), "next group is page 2 (blank page skipped)");
    AssertTrue(CommitFake(second, "worker-b"), "page 2 group commits");

    sched.Seal();
    ClusterNerAssignment last = sched.Claim("worker-a", now);
    AssertTrue(last.Kind == ClusterNerClaimKind.Group && last.Pages.SequenceEqual([4]), "tail group is page 4 after seal");
    AssertTrue(string.IsNullOrEmpty(last.Lookahead), "last group has no next page");
    AssertTrue(CommitFake(last, "worker-a"), "tail group commits");
    AssertTrue(sched.IsComplete, "every non-empty page's group completed");
    AssertTrue(sched.Claim("no-key", now).Kind == ClusterNerClaimKind.Done, "no-key node sees the job done");

    OcrEntities merged = sched.Merge();
    EntityHit? company = merged.Companies.SingleOrDefault(c => c.Name == "北京华腾科技有限公司");
    EntityHit? person = merged.Persons.SingleOrDefault(p => p.Name == "张伟");
    AssertTrue(company is not null && person is not null, "merge keeps the company and the person");
    AssertTrue(merged.Companies.Count == 1 && merged.Persons.Count == 1, "duplicate LLM returns collapse to one name each");
    AssertTrue(company!.Pages.SequenceEqual([1, 4]) && company.Count == 2,
        $"joined company is on pages 1 and 4 once each (pages={string.Join(",", company.Pages)} count={company.Count})");
    AssertTrue(person!.Pages.SequenceEqual([2, 4]) && person.Count == 2,
        $"person stays on the pages that contain it (pages={string.Join(",", person.Pages)} count={person.Count})");

    ChallengeFileResult protocol = ChallengeResultMapper.BuildFileResult(
        "f1",
        doc.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList(),
        merged.Companies.Select(c => c.Name).ToList(),
        merged.Persons.Select(p => p.Name).ToList());
    int originCount = protocol.Pages.SelectMany(p => p.RuleList).SelectMany(r => r.RuleItemList).Sum(i => i.OriginText.Count);
    AssertTrue(originCount >= 2, "coordinator protocol output still has originText");
}

Console.WriteLine("=== NER failure and lease expiry ===");
{
    DateTimeOffset now = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    ClusterNerScheduler failSched = new(new ClusterNerOptions
    {
        PageCount = 1,
        PagesPerRequest = 1,
        MaxChars = 10_000,
        LeaseMs = 5_000,
        LocalNodeId = "coord",
    });
    failSched.SetNerCapacity("worker-a", 1);
    failSched.SetNerCapacity("worker-b", 1);
    failSched.AddPage(new OcrPageResult { Page = 1, Text = "甲方北京华腾科技有限公司与张伟签订" });
    failSched.Seal();
    ClusterNerAssignment owned = failSched.Claim("worker-a", now);
    AssertTrue(owned.Kind == ClusterNerClaimKind.Group, "failure fixture leased the only group");
    failSched.Fail(owned.GroupId, "worker-a");
    ClusterNerAssignment again = failSched.Claim("worker-a", now);
    AssertTrue(again.Kind == ClusterNerClaimKind.Wait, "the node that just failed waits while another LLM node can take the group");
    ClusterNerAssignment retry = failSched.Claim("worker-b", now);
    AssertTrue(retry.Kind == ClusterNerClaimKind.Group && retry.GroupId == owned.GroupId, "another node redoes the failed group");
    AssertTrue(!failSched.TryComplete(owned.GroupId, "worker-a", ["北京华腾科技有限公司"], ["张伟"], null),
        "late result from the failed node is rejected");
    AssertTrue(failSched.TryComplete(retry.GroupId, "worker-b", ["北京华腾科技有限公司"], ["张伟"], null),
        "the retry node commits");

    ClusterNerScheduler leaseSched = new(new ClusterNerOptions
    {
        PageCount = 1,
        PagesPerRequest = 1,
        MaxChars = 10_000,
        LeaseMs = 5_000,
        LocalNodeId = "coord",
    });
    leaseSched.SetNerCapacity("worker-a", 1);
    leaseSched.SetNerCapacity("worker-b", 1);
    leaseSched.AddPage(new OcrPageResult { Page = 1, Text = "页二正文里有张伟和一段足够长的说明文字" });
    leaseSched.Seal();
    ClusterNerAssignment held = leaseSched.Claim("worker-a", now);
    AssertTrue(held.Kind == ClusterNerClaimKind.Group, "expiry fixture leased the group");
    ClusterNerAssignment tooSoon = leaseSched.Claim("worker-b", now.AddSeconds(1));
    AssertTrue(tooSoon.Kind == ClusterNerClaimKind.Wait, "the other node waits while the NER lease holds");
    ClusterNerAssignment expired = leaseSched.Claim("worker-b", now.AddMilliseconds(held.LeaseMs + 1));
    AssertTrue(expired.Kind == ClusterNerClaimKind.Group && expired.GroupId == held.GroupId, "expired NER lease is retried by the other node");
    ClusterLeaseExpiry[] expiries = leaseSched.DrainExpiries();
    AssertTrue(expiries.Length == 1 && expiries[0].NodeId == "worker-a" && expiries[0].Pages.SequenceEqual(held.Pages),
        "NER lease expiry is recorded");
    AssertTrue(!leaseSched.TryComplete(held.GroupId, "worker-a", ["张伟"], null, null), "expired owner cannot commit");
    AssertTrue(leaseSched.TryComplete(expired.GroupId, "worker-b", ["张伟"], null, null), "the node that retried the lease commits");
    AssertTrue(leaseSched.IsComplete, "job completes on the retry node");
}

Console.WriteLine("=== NER capacity and give-up ===");
{
    ClusterNerScheduler sched = new(new ClusterNerOptions
    {
        PageCount = 6,
        PagesPerRequest = 1,
        MaxChars = 50_000,
        LeaseMs = 30_000,
        MaxAttempts = 2,
        LocalNodeId = "coord",
    });
    sched.SetNerCapacity("fast", 2);
    sched.SetNerCapacity("slow", 1);
    for (int page = 1; page <= 6; page++)
        sched.AddPage(new OcrPageResult { Page = page, Text = "页" + page + "正文足够长以便分组" });
    sched.Seal();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterNerAssignment a = sched.Claim("fast", now);
    ClusterNerAssignment b = sched.Claim("fast", now);
    ClusterNerAssignment c = sched.Claim("fast", now);
    AssertTrue(a.Kind == ClusterNerClaimKind.Group && b.Kind == ClusterNerClaimKind.Group, "fast node holds two groups");
    AssertTrue(c.Kind == ClusterNerClaimKind.Wait, "fast node cannot exceed its NER concurrency");
    AssertTrue(sched.TryComplete(a.GroupId, "fast", [], [], []), "release one fast slot");
    AssertTrue(sched.TryComplete(b.GroupId, "fast", [], [], []), "release the other fast slot");

    int fastDone = 2;
    int slowDone = 0;
    int guard = 0;
    while (!sched.IsComplete && guard++ < 20)
    {
        ClusterNerAssignment fast = sched.Claim("fast", now);
        if (fast.Kind == ClusterNerClaimKind.Group &&
            sched.TryComplete(fast.GroupId, "fast", [], [], []))
            fastDone++;
        ClusterNerAssignment fast2 = sched.Claim("fast", now);
        if (fast2.Kind == ClusterNerClaimKind.Group &&
            sched.TryComplete(fast2.GroupId, "fast", [], [], []))
            fastDone++;
        ClusterNerAssignment slow = sched.Claim("slow", now);
        if (slow.Kind == ClusterNerClaimKind.Group &&
            sched.TryComplete(slow.GroupId, "slow", [], [], []))
            slowDone++;
    }

    AssertTrue(sched.IsComplete && fastDone + slowDone == 6, $"all 6 groups finished ({fastDone}+{slowDone})");
    AssertTrue(fastDone > slowDone, $"higher NER concurrency finishes more groups (fast={fastDone} slow={slowDone})");

    ClusterNerScheduler poison = new(new ClusterNerOptions
    {
        PageCount = 1,
        PagesPerRequest = 1,
        MaxChars = 10_000,
        LeaseMs = 5_000,
        MaxAttempts = 2,
        LocalNodeId = "coord",
    });
    poison.SetNerCapacity("only", 1);
    poison.AddPage(new OcrPageResult { Page = 1, Text = "只有一页的合同正文" });
    poison.Seal();
    ClusterNerAssignment bad = poison.Claim("only", now);
    poison.Fail(bad.GroupId, "only");
    ClusterNerAssignment bad2 = poison.Claim("only", now);
    AssertTrue(bad2.Kind == ClusterNerClaimKind.Group && bad2.GroupId == bad.GroupId, "the only LLM node may retry");
    poison.Fail(bad2.GroupId, "only");
    AssertTrue(poison.IsComplete, "giving up after max attempts still finishes the job");
    AssertTrue(poison.DrainGiveUps().Length == 1, "give-up is reported");
    AssertTrue(poison.Merge().Companies.Count == 0 && poison.Merge().Persons.Count == 0, "abandoned group adds no names");

    int singleWaves = (200 + 8 - 1) / 8;
    int tripleWaves = (200 + 24 - 1) / 24;
    AssertTrue(tripleWaves < singleWaves && singleWaves >= tripleWaves * 2,
        $"200 groups: 1x8 needs {singleWaves} waves, 3x8 needs {tripleWaves} (~{singleWaves / (double)tripleWaves:F1}x)");
    Console.WriteLine(
        $"  estimated LLM-stage speedup for 2000 non-empty pages / 10: {singleWaves} waves on one node vs {tripleWaves} waves on three ({singleWaves / (double)tripleWaves:F1}x), assuming each node has its own key and the provider accepts the aggregate concurrency");
}

Console.WriteLine("=== NER fair dispatch ===");
{
    DateTimeOffset now = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    ClusterNerScheduler fair = new(new ClusterNerOptions
    {
        PageCount = 4,
        PagesPerRequest = 1,
        MaxChars = 50_000,
        LeaseMs = 30_000,
        LocalNodeId = "coord",
        FairHoldMs = 5_000,
        ActiveMs = 5_000,
    });
    fair.SetNerCapacity("coord", 8);
    fair.SetNerCapacity("w1", 8);
    fair.SetNerCapacity("w2", 8);
    for (int page = 1; page <= 4; page++)
        fair.AddPage(new OcrPageResult { Page = page, Text = "第" + page + "页正文" });
    fair.Seal();

    ClusterNerAssignment first = fair.Claim("coord", now);
    AssertTrue(first.Kind == ClusterNerClaimKind.Group, "fair dispatch still gives the coordinator the first tied group");
    ClusterNerAssignment hog = fair.Claim("coord", now);
    AssertTrue(hog.Kind == ClusterNerClaimKind.Wait, "coordinator yields while reserved workers have a lower load");
    ClusterNerAssignment w1 = fair.Claim("w1", now);
    ClusterNerAssignment w2 = fair.Claim("w2", now);
    AssertTrue(w1.Kind == ClusterNerClaimKind.Group && w2.Kind == ClusterNerClaimKind.Group && w1.GroupId != w2.GroupId,
        "each waiting worker receives its own group");
    ClusterNerAssignment again = fair.Claim("coord", now.AddSeconds(1));
    AssertTrue(again.Kind == ClusterNerClaimKind.Group, "once loads match, the coordinator can take the next group");

    ClusterNerScheduler expiredHold = new(new ClusterNerOptions
    {
        PageCount = 2,
        PagesPerRequest = 1,
        MaxChars = 50_000,
        LeaseMs = 30_000,
        LocalNodeId = "coord",
        FairHoldMs = 1_000,
    });
    expiredHold.SetNerCapacity("coord", 8);
    expiredHold.SetNerCapacity("absent", 8);
    expiredHold.AddPage(new OcrPageResult { Page = 1, Text = "第一页正文" });
    expiredHold.AddPage(new OcrPageResult { Page = 2, Text = "第二页正文" });
    expiredHold.Seal();
    AssertTrue(expiredHold.Claim("coord", now).Kind == ClusterNerClaimKind.Group, "hold starts by giving the local node one group");
    AssertTrue(expiredHold.Claim("coord", now).Kind == ClusterNerClaimKind.Wait, "local node waits inside the fair-hold window");
    AssertTrue(expiredHold.Claim("coord", now.AddMilliseconds(1_500)).Kind == ClusterNerClaimKind.Group,
        "after the fair-hold window a worker that never claimed is no longer reserved");
}

Console.WriteLine("=== NER fair dispatch under 2s latency ===");
{
    const int groups = 6;
    const int llmMs = 2_000;
    string[] cluster = ["coord", "w1", "w2"];

    (Dictionary<string, int> ByNode, double WallMs) before = SimulateNer(fair: false, cap: 8, groups, llmMs, workerLag: true, cluster).GetAwaiter().GetResult();
    (Dictionary<string, int> ByNode, double WallMs) afterWide = SimulateNer(fair: true, cap: 8, groups, llmMs, workerLag: false, cluster).GetAwaiter().GetResult();
    (Dictionary<string, int> ByNode, double WallMs) single = SimulateNer(fair: true, cap: 2, groups, llmMs, workerLag: false, ["coord"]).GetAwaiter().GetResult();
    (Dictionary<string, int> ByNode, double WallMs) after = SimulateNer(fair: true, cap: 2, groups, llmMs, workerLag: false, cluster).GetAwaiter().GetResult();

    int Count(Dictionary<string, int> map, string node) => map.GetValueOrDefault(node);
    string Spread(Dictionary<string, int> map) =>
        "coord=" + Count(map, "coord") + " w1=" + Count(map, "w1") + " w2=" + Count(map, "w2");

    Console.WriteLine($"  before FCFS cap=8 workerPoll=500ms: {Spread(before.ByNode)} wallMs={before.WallMs:F0}");
    Console.WriteLine($"  after fair long-poll cap=8: {Spread(afterWide.ByNode)} wallMs={afterWide.WallMs:F0}");
    Console.WriteLine($"  single node cap=2: {Spread(single.ByNode)} wallMs={single.WallMs:F0}");
    Console.WriteLine($"  after fair long-poll cap=2 x3: {Spread(after.ByNode)} wallMs={after.WallMs:F0} speedup={single.WallMs / Math.Max(1, after.WallMs):F2}x vs single");

    AssertTrue(Count(before.ByNode, "coord") == groups && Count(before.ByNode, "w1") == 0 && Count(before.ByNode, "w2") == 0,
        "before the fix, a coordinator under its cap leases every group");
    AssertTrue(Count(afterWide.ByNode, "coord") < groups && Count(afterWide.ByNode, "w1") > 0 && Count(afterWide.ByNode, "w2") > 0,
        "after the fix, the same cap spreads groups off the coordinator");
    AssertTrue(Count(after.ByNode, "coord") > 0 && Count(after.ByNode, "w1") > 0 && Count(after.ByNode, "w2") > 0
        && Count(after.ByNode, "coord") + Count(after.ByNode, "w1") + Count(after.ByNode, "w2") == groups,
        "1 coordinator + 2 workers each finish some of the 6 groups");
    AssertTrue(after.WallMs < single.WallMs * 0.6,
        $"fair cluster wall {after.WallMs:F0}ms beats single-node wall {single.WallMs:F0}ms");

    string ner = ClusterJobLog.FormatNer(new ClusterNerSnapshot
    {
        Done = groups,
        Formed = groups,
        InFlight = 0,
        Sealed = true,
        Complete = true,
        Nodes =
        [
            new ClusterNodeLoad { NodeId = "coord", PagesCommitted = Count(after.ByNode, "coord") },
            new ClusterNodeLoad { NodeId = "w1", PagesCommitted = Count(after.ByNode, "w1") },
            new ClusterNodeLoad { NodeId = "w2", PagesCommitted = Count(after.ByNode, "w2") },
        ],
    });
    string doneLine = "Cluster job job done: pages=6 elapsedMs=2000 3.0 pages/s byNode=coord=2 " + ner;
    AssertTrue(doneLine.Contains("nerByNode=", StringComparison.Ordinal)
        && doneLine.Contains("coord=", StringComparison.Ordinal)
        && doneLine.Contains("w1=", StringComparison.Ordinal)
        && doneLine.Contains("w2=", StringComparison.Ordinal),
        "final job log line includes per-node NER counts (" + ner + ")");
}

Console.WriteLine("=== NER dispatch logs stay quiet ===");
{
    var lines = new List<(LogLevel Level, string Text)>();
    var info = new MemoryLogger(lines, LogLevel.Information);
    ClusterJobLog.NerClaim(info, verbose: false, "job", "worker-a", "ner-1", "1,2", 20_000);
    ClusterJobLog.NerDone(info, verbose: false, "worker-a", "job", "ner-1", 2);
    AssertTrue(lines.Count == 0, "NER claim/done are hidden at Information by default");
    var debug = new MemoryLogger(lines, LogLevel.Debug);
    ClusterJobLog.NerClaim(debug, verbose: false, "job", "worker-a", "ner-1", "1,2", 20_000);
    AssertTrue(lines.Any(l => l.Level == LogLevel.Debug && l.Text.Contains("NER claim", StringComparison.Ordinal)),
        "NER claim is Debug");
    ClusterJobLog.Progress(info, "job", 4, 10, 40, 1.5, 2, "coord=4", "ner=1/2 nerInFlight=1 nerByNode=worker-a=1");
    AssertTrue(lines.Any(l => l.Level == LogLevel.Information && l.Text.Contains("ner=1/2", StringComparison.Ordinal)),
        "progress summary includes NER progress");
}

async Task<(Dictionary<string, int> ByNode, double WallMs)> SimulateNer(
    bool fair,
    int cap,
    int groups,
    int llmMs,
    bool workerLag,
    string[] nodes)
{
    var sched = new ClusterNerScheduler(new ClusterNerOptions
    {
        PageCount = groups,
        PagesPerRequest = 1,
        MaxChars = 100_000,
        LeaseMs = 60_000,
        LocalNodeId = "coord",
        FairDispatch = fair,
        FairHoldMs = fair ? 5_000 : 0,
        ActiveMs = 5_000,
    });
    foreach (string node in nodes)
        sched.SetNerCapacity(node, cap);
    for (int page = 1; page <= groups; page++)
        sched.AddPage(new OcrPageResult { Page = page, Text = "第" + page + "页正文足够组成一组" });
    sched.Seal();

    var done = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var pumps = new List<Task>(nodes.Length * cap);
    Stopwatch wall = Stopwatch.StartNew();
    foreach (string node in nodes)
    {
        bool lag = workerLag && !string.Equals(node, "coord", StringComparison.Ordinal);
        for (int i = 0; i < cap; i++)
        {
            string id = node;
            pumps.Add(Task.Run(() => PumpNer(sched, id, llmMs, lag, done, cts.Token), cts.Token));
        }
    }

    await Task.WhenAll(pumps).ConfigureAwait(false);
    wall.Stop();
    return (done.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), wall.Elapsed.TotalMilliseconds);
}

async Task PumpNer(
    ClusterNerScheduler sched,
    string node,
    int llmMs,
    bool lag,
    ConcurrentDictionary<string, int> done,
    CancellationToken ct)
{
    if (lag)
        await Task.Delay(500, ct).ConfigureAwait(false);
    while (!ct.IsCancellationRequested)
    {
        if (sched.IsComplete)
            return;
        ClusterNerAssignment claim = sched.Claim(node, DateTimeOffset.UtcNow);
        if (claim.Kind == ClusterNerClaimKind.Done)
            return;
        if (claim.Kind != ClusterNerClaimKind.Group)
        {
            if (lag)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            else
            {
                sched.EnterWait(node, DateTimeOffset.UtcNow);
                try
                {
                    await sched.WaitForChangeAsync(claim.Version, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                }
                finally
                {
                    sched.LeaveWait(node);
                }
            }

            continue;
        }

        await Task.Delay(llmMs, ct).ConfigureAwait(false);
        if (sched.TryComplete(claim.GroupId, node, [], [], []))
            done.AddOrUpdate(node, 1, static (_, count) => count + 1);
    }
}

Console.WriteLine("=== adaptive batch and NER blocker ===");
{
    DateTimeOffset t0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    ClusterPageScheduler sched = NewScheduler(40, expectedNodes: 3, leaseFloorMs: 60_000, pageTimeoutMs: 60_000);
    sched.SetCapacity("slow", 16);
    sched.SetCapacity("idle", 8);
    sched.SetNerBlocker(1);
    ClusterClaim first = sched.Claim("slow", 16, t0);
    AssertTrue(first.Kind == ClusterClaimKind.Batch && first.Pages.Length == 2, "new node starts with a small batch");
    AssertTrue(first.Pages[0] == 1, "blocker page is leased first");
    foreach (int page in first.Pages)
        AssertTrue(sched.TryCommit(first.BatchId, page, t0.AddSeconds(20)), "commit slow first batch");
    ClusterClaim second = sched.Claim("slow", 16, t0.AddSeconds(20));
    AssertTrue(second.Kind == ClusterClaimKind.Batch && second.Pages.Length <= 2, $"measured slow node stays small ({second.Pages.Length})");
    sched.SetNerBlocker(second.Pages.Min());
    ClusterClaim tooSoon = sched.Claim("idle", 8, t0.AddSeconds(21));
    AssertTrue(tooSoon.Kind == ClusterClaimKind.Batch && !tooSoon.Speculative, "fresh lease is not copied yet");
    int blocker = second.Pages.Min();
    ClusterClaim copy = sched.Claim("idle", 8, t0.AddSeconds(20).AddMilliseconds(20_000));
    AssertTrue(copy.Kind == ClusterClaimKind.Batch && copy.Speculative, "stale NER blocker is copied");
    AssertTrue(copy.Pages.Length == 1 && copy.Pages[0] == blocker, "copy is only the blocking page");
    AssertTrue(sched.TryCommit(copy.BatchId, blocker, t0.AddSeconds(41)), "first commit of the copy wins");
    AssertTrue(!sched.TryCommit(second.BatchId, blocker, t0.AddSeconds(50)), "original lease cannot overwrite the copy");
}

Console.WriteLine("=== fast node grows after a quick batch ===");
{
    DateTimeOffset t0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    ClusterPageScheduler sched = NewScheduler(30, expectedNodes: 2);
    sched.SetCapacity("fast", 8);
    ClusterClaim first = sched.Claim("fast", 8, t0);
    AssertTrue(first.Pages.Length == 2, "fast node also starts small");
    foreach (int page in first.Pages)
        sched.TryCommit(first.BatchId, page, t0.AddMilliseconds(200));
    ClusterClaim second = sched.Claim("fast", 8, t0.AddMilliseconds(200));
    AssertTrue(second.Pages.Length == 8, $"fast node opens up to capacity ({second.Pages.Length})");
}

Console.WriteLine("=== dispatch rules ===");
{
    AssertTrue(ClusterDispatchRules.AtSessionCap(2) && !ClusterDispatchRules.AtSessionCap(1), "two live sessions is the cap");
    AssertTrue(ClusterDispatchRules.SessionCount(0, 3) == 0, "reported session count wins over the skip list");
    AssertTrue(ClusterDispatchRules.SessionCount(null, 2) == 2, "older workers fall back to listed jobs");
    AssertTrue(ClusterDispatchRules.ShouldOfferJob(false, false, false), "ocr still open is always offered");
    AssertTrue(!ClusterDispatchRules.ShouldOfferJob(true, true, false), "ocr done and no NER capacity is not offered");
    AssertTrue(!ClusterDispatchRules.ShouldOfferJob(true, false, true), "ocr done and NER idle is not offered");
    AssertTrue(ClusterDispatchRules.ShouldOfferJob(true, true, true), "ocr done with NER left goes to an LLM node");
}

Console.WriteLine("=== pdf range and read cancel ===");
{
    AssertTrue(ClusterPdfRange.TrySlice(1000, 0, 99, out long s, out long e) && s == 0 && e == 99, "closed range");
    AssertTrue(ClusterPdfRange.TrySlice(1000, 900, null, out s, out e) && s == 900 && e == 999, "open end is clamped");
    AssertTrue(ClusterPdfRange.TrySlice(1000, null, 10, out s, out e) && s == 990 && e == 999, "suffix range");
    AssertTrue(!ClusterPdfRange.TrySlice(1000, 1000, 1001, out _, out _), "start past end is rejected");

    ClusterPageScheduler sched = NewScheduler(2);
    var job = new ClusterJob("job-cancel-1", sched, new byte[8], 8, 2, 96, "http://files.example/a.pdf");
    ClusterJob.PdfReadLease? lease = job.TryEnterPdfRead();
    AssertTrue(lease is not null, "read lease acquired");
    Task reader = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(Timeout.Infinite, lease!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lease!.Dispose();
        }
    });
    var waitSw = Stopwatch.StartNew();
    job.StopNewPdfReads();
    await job.WaitForPdfReadersAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    waitSw.Stop();
    await reader.ConfigureAwait(false);
    AssertTrue(waitSw.Elapsed < TimeSpan.FromSeconds(2), $"cancelling the PDF stream released the reader ({waitSw.ElapsedMilliseconds} ms)");
    AssertTrue(job.TryEnterPdfRead() is null, "finished job rejects new readers");
    AssertTrue(job.SourceUrl == "http://files.example/a.pdf", "source url is kept on the job");
}

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    return 1;
}

Console.WriteLine("ALL PASSED");
return 0;

sealed class MemoryLogger : ILogger
{
    private readonly List<(LogLevel Level, string Text)> _lines;
    private readonly LogLevel _min;

    public MemoryLogger(List<(LogLevel Level, string Text)> lines, LogLevel min)
    {
        _lines = lines;
        _min = min;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _min;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        _lines.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
