/// <summary>
/// Virtual-clock replay of a 463-page scan on three 12-thread nodes.
/// Uses the real <see cref="MiniOcr.Services.ClusterPageScheduler"/>. OCR and render
/// durations are parameters, not a paddle run: the 265 MB file is not in this tree.
/// </summary>
internal static class ClusterReplay
{
    private const int Pages = 463;
    private const int Engines = 6;
    private const int Renderers = 4;

    public static int Run()
    {
        Console.WriteLine("=== 463-page cluster replay (3 nodes, 6 engines, 4 renderers) ===");
        Console.WriteLine("Download of 265 MB is not on this clock: prefetch (PR #17) overlaps it.");
        Console.WriteLine("At 20 MB/s that file is 13250 ms; at the bench's 2 MB/s throttle it is 132500 ms.");
        Console.WriteLine();

        Calib scan = new("dpi130-scan", RenderMs: 45, OcrMs: 650, ClaimRtt: 25, PostMs: 12, RetryMs: 200);
        Calib light = new("dpi96-readme-scan", RenderMs: 15, OcrMs: 390, ClaimRtt: 25, PostMs: 12, RetryMs: 200);
        // 4-core box, 1 engine, samples/sample-multipage.pdf, det 960, cls off, warm pass.
        // dpi 130: render 18 ms/page in-process, OCR 88 ms/page. Not the 265 MB scan.
        Calib text = new("sample-text-dpi130", RenderMs: 20, OcrMs: 90, ClaimRtt: 25, PostMs: 12, RetryMs: 200);
        foreach (Calib calib in new[] { scan, light, text })
        {
            Console.WriteLine($"-- {calib.Name}  render={calib.RenderMs}ms  ocr={calib.OcrMs}ms/engine  claimRtt={calib.ClaimRtt}ms  post={calib.PostMs}ms  equal nodes --");
            Report[] rows =
            [
                Run(calib, "legacy serial", pipeline: false, ahead: 0, stale: false, slow: 1),
                Run(calib, "per-page post", pipeline: true, ahead: 0, stale: false, slow: 1),
                Run(calib, "post+ahead 4", pipeline: true, ahead: 4, stale: false, slow: 1),
                Run(calib, "ahead+stale", pipeline: true, ahead: 4, stale: true, slow: 1),
            ];
            Print(rows);
            Console.WriteLine($"-- {calib.Name}  third node OCR x1.5 --");
            Report[] hetero =
            [
                Run(calib, "legacy serial", pipeline: false, ahead: 0, stale: false, slow: 1.5),
                Run(calib, "post+ahead 4", pipeline: true, ahead: 4, stale: false, slow: 1.5),
                Run(calib, "ahead+stale", pipeline: true, ahead: 4, stale: true, slow: 1.5),
            ];
            Print(hetero);
            Console.WriteLine($"-- {calib.Name}  third node hung (OCR x8, one wave exceeds 2.5s) --");
            Report[] hung =
            [
                Run(calib, "legacy serial", pipeline: false, ahead: 0, stale: false, slow: 8),
                Run(calib, "post+ahead 4", pipeline: true, ahead: 4, stale: false, slow: 8),
                Run(calib, "ahead+stale", pipeline: true, ahead: 4, stale: true, slow: 8),
            ];
            Print(hung);
        }

        return 0;
    }

    private static void Print(Report[] rows)
    {
        Console.WriteLine(
            $"{"mode",-16} {"wall",8} {"util",6} {"stall",8} {"tail",8} {"claims",7} {"posts",7} {"spec",6} {"waste",6}");
        long baseline = rows[0].WallMs;
        foreach (Report row in rows)
        {
            double util = row.WallMs <= 0 ? 0 : 100.0 * row.OcrBusyMs / (row.WallMs * 3 * Engines);
            long saved = baseline - row.WallMs;
            Console.WriteLine(
                $"{row.Name,-16} {row.WallMs,8} {util,5:F1}% {row.BitmapStallMs,8} {row.TailMs,8} {row.Claims,7} {row.Posts,7} {row.Speculative,6} {row.WastedOcr,6}  {saved,6} ms");
        }

        Console.WriteLine();
    }

    private static Report Run(Calib calib, string name, bool pipeline, int ahead, bool stale, double slow)
    {
        var scheduler = new MiniOcr.Services.ClusterPageScheduler(new MiniOcr.Services.ClusterScheduleOptions
        {
            PageCount = Pages,
            LocalNodeId = "coord",
            LeaseFloorMs = 60_000,
            PageTimeoutMs = 20_000,
            LeaseCapMs = 180_000,
            SpeculativeTailPages = 4,
            ExpectedNodes = 3,
            RenderAheadPages = ahead,
            SpeculativeStaleLeases = stale,
        });
        string[] ids = ["coord", "a", "b"];
        double[] scale = [1, 1, slow];
        var nodes = new Node[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            scheduler.SetCapacity(ids[i], Engines);
            nodes[i] = new Node(ids[i], scale[i], calib);
        }

        var sim = new Sim(scheduler, nodes, calib, pipeline);
        for (int i = 0; i < nodes.Length; i++)
            sim.ScheduleClaim(nodes[i], 0);
        sim.Run();
        long pendingEmpty = sim.PendingEmptyAt < 0 ? sim.Now : sim.PendingEmptyAt;
        return new Report(
            name,
            sim.Now,
            sim.OcrBusyMs,
            sim.BitmapStallMs,
            Math.Max(0, sim.Now - pendingEmpty),
            sim.Claims,
            sim.Posts,
            sim.Speculative,
            sim.WastedOcr);
    }

    private sealed class Sim
    {
        private readonly MiniOcr.Services.ClusterPageScheduler _scheduler;
        private readonly Node[] _nodes;
        private readonly Calib _calib;
        private readonly bool _pipeline;
        private readonly PriorityQueue<Action, (long Time, int Seq)> _queue = new();
        private readonly DateTimeOffset _epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _seq;
        private int _donePages;

        public long Now { get; private set; }
        public long OcrBusyMs { get; private set; }
        public long BitmapStallMs { get; private set; }
        public long PendingEmptyAt { get; private set; } = -1;
        public int Claims { get; private set; }
        public int Posts { get; private set; }
        public int Speculative { get; private set; }
        public int WastedOcr { get; private set; }

        public Sim(MiniOcr.Services.ClusterPageScheduler scheduler, Node[] nodes, Calib calib, bool pipeline)
        {
            _scheduler = scheduler;
            _nodes = nodes;
            _calib = calib;
            _pipeline = pipeline;
        }

        public void ScheduleClaim(Node node, long time)
        {
            if (node.Stopped)
                return;
            int gen = ++node.ClaimGen;
            At(time, () =>
            {
                if (gen != node.ClaimGen || node.Stopped)
                    return;
                Claim(node);
            });
        }

        public void Run()
        {
            int guard = 0;
            while (_queue.TryDequeue(out Action? act, out (long Time, int Seq) key))
            {
                if (++guard > 2_000_000)
                    throw new InvalidOperationException("replay did not finish");
                Now = key.Time;
                act();
                if (_donePages >= Pages)
                    break;
            }
        }

        private void Claim(Node node)
        {
            var claim = _scheduler.Claim(node.Id, Engines, _epoch.AddMilliseconds(Now));
            Claims++;
            if (claim.Kind == MiniOcr.Services.ClusterClaimKind.Done)
            {
                node.Stopped = true;
                return;
            }

            if (claim.Kind == MiniOcr.Services.ClusterClaimKind.Wait)
            {
                ScheduleClaim(node, Now + _calib.RetryMs);
                return;
            }

            if (claim.Speculative)
                Speculative += claim.Pages.Length;
            if (_pipeline)
            {
                foreach (int page in claim.Pages)
                    node.Waiting.Enqueue(new Work(claim.BatchId, page));
                Pump(node);
                ScheduleClaim(node, Now + _calib.ClaimRtt);
                return;
            }

            long doneAt = RunSerialBatch(node, Now, claim.Pages.Length);
            long postAt = doneAt + _calib.PostMs;
            Posts++;
            At(postAt, () =>
            {
                foreach (int page in claim.Pages)
                    Commit(claim.BatchId, page, wonSpeculative: claim.Speculative);
                ScheduleClaim(node, postAt + _calib.ClaimRtt);
            });
        }

        private long RunSerialBatch(Node node, long start, int count)
        {
            long[] renderFree = new long[Renderers];
            long[] ocrFree = new long[Engines];
            for (int i = 0; i < Renderers; i++)
                renderFree[i] = start;
            for (int i = 0; i < Engines; i++)
                ocrFree[i] = start;
            long end = start;
            long ocr = (long)Math.Round(_calib.OcrMs * node.Scale);
            for (int n = 0; n < count; n++)
            {
                int slot = SlowestIndex(renderFree, earliest: true);
                long ready = renderFree[slot] + _calib.RenderMs;
                renderFree[slot] = ready;
                int engine = SlowestIndex(ocrFree, earliest: true);
                long stall = Math.Max(0, ready - ocrFree[engine]);
                long begin = Math.Max(ocrFree[engine], ready);
                BitmapStallMs += stall;
                OcrBusyMs += ocr;
                ocrFree[engine] = begin + ocr;
                if (ocrFree[engine] > end)
                    end = ocrFree[engine];
            }

            return end;
        }

        private void Pump(Node node)
        {
            while (node.Waiting.Count > 0)
            {
                int slot = SlowestIndex(node.RenderFree, earliest: true);
                if (node.RenderFree[slot] > Now && node.RenderBusy == Renderers)
                    return;
                if (node.RenderFree[slot] > Now)
                    return;
                Work work = node.Waiting.Dequeue();
                node.RenderFree[slot] = Now + _calib.RenderMs;
                node.RenderBusy++;
                long ready = node.RenderFree[slot];
                At(ready, () =>
                {
                    node.RenderBusy--;
                    StartOcr(node, work, ready);
                    Pump(node);
                });
            }
        }

        private void StartOcr(Node node, Work work, long ready)
        {
            int engine = SlowestIndex(node.OcrFree, earliest: true);
            long engineFree = node.OcrFree[engine];
            if (engineFree < ready)
                BitmapStallMs += ready - engineFree;
            long begin = Math.Max(engineFree, ready);
            long ocr = (long)Math.Round(_calib.OcrMs * node.Scale);
            long ocrDone = begin + ocr;
            OcrBusyMs += ocr;
            node.OcrFree[engine] = ocrDone;
            At(ocrDone, () =>
            {
                long posted = ocrDone + _calib.PostMs;
                At(posted, () =>
                {
                    Posts++;
                    Commit(work.BatchId, work.Page, wonSpeculative: false);
                    ScheduleClaim(node, posted + _calib.ClaimRtt);
                });
            });
        }

        private void Commit(string batchId, int page, bool wonSpeculative)
        {
            bool won = _scheduler.TryCommit(batchId, page, _epoch.AddMilliseconds(Now));
            if (!won)
            {
                WastedOcr++;
                return;
            }

            _ = wonSpeculative;
            _donePages++;
            var snap = _scheduler.Snapshot();
            if (PendingEmptyAt < 0 && snap.Pending == 0 && snap.Done < Pages)
                PendingEmptyAt = Now;
        }

        private void At(long time, Action act)
        {
            int seq = _seq++;
            _queue.Enqueue(act, (time, seq));
        }

        private static int SlowestIndex(long[] times, bool earliest)
        {
            int best = 0;
            for (int i = 1; i < times.Length; i++)
            {
                if (earliest ? times[i] < times[best] : times[i] > times[best])
                    best = i;
            }

            return best;
        }
    }

    private sealed class Node
    {
        public Node(string id, double scale, Calib calib)
        {
            Id = id;
            Scale = scale;
            _ = calib;
            RenderFree = new long[Renderers];
            OcrFree = new long[Engines];
        }

        public string Id { get; }
        public double Scale { get; }
        public long[] RenderFree { get; }
        public long[] OcrFree { get; }
        public int RenderBusy { get; set; }
        public Queue<Work> Waiting { get; } = new();
        public int ClaimGen { get; set; }
        public bool Stopped { get; set; }
    }

    private readonly record struct Work(string BatchId, int Page);

    private readonly record struct Calib(string Name, long RenderMs, long OcrMs, long ClaimRtt, long PostMs, long RetryMs);

    private readonly record struct Report(
        string Name,
        long WallMs,
        long OcrBusyMs,
        long BitmapStallMs,
        long TailMs,
        int Claims,
        int Posts,
        int Speculative,
        int WastedOcr);
}
