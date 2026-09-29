using Microsoft.Extensions.Logging.Abstractions;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>Identity this process advertises to the rest of the cluster.</summary>
public sealed class ClusterSelf
{
    public required string NodeId { get; init; }
    public required string Role { get; init; }
    public required int Capacity { get; init; }
    public required int EngineCount { get; init; }
    public required string OcrMode { get; init; }
    public required string Model { get; init; }
    public required int Dpi { get; init; }
    public required string AdvertiseUrl { get; init; }
}

/// <summary>Coordinator's view of local + remote nodes. Thread-safe.</summary>
public sealed class ClusterNodeRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly ClusterSelf _self;
    private readonly int _heartbeatFreshMs;
    private readonly ILogger<ClusterNodeRegistry> _logger;

    public ClusterNodeRegistry(ClusterSelf self, int heartbeatFreshMs, ILogger<ClusterNodeRegistry>? logger = null)
    {
        _self = self;
        _heartbeatFreshMs = Math.Max(1000, heartbeatFreshMs);
        _logger = logger ?? NullLogger<ClusterNodeRegistry>.Instance;
        _nodes[self.NodeId] = new Node
        {
            Id = self.NodeId,
            Local = true,
            Healthy = true,
            Capacity = Math.Max(1, self.Capacity),
            OcrMode = self.OcrMode,
            Model = self.Model,
            Dpi = self.Dpi,
            Url = string.IsNullOrWhiteSpace(self.AdvertiseUrl) ? null : self.AdvertiseUrl,
        };
    }

    public ClusterSelf Self => _self;
    public int LocalCapacity
    {
        get { lock (_gate) return _nodes[_self.NodeId].Capacity; }
    }

    public bool HasPotentialRemote
    {
        get
        {
            lock (_gate)
                return _nodes.Values.Any(n => !n.Local && !n.Hidden);
        }
    }

    public void SeedWorker(string url, int capacity)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        lock (_gate)
        {
            if (_nodes.Values.Any(n => !n.Local && UrlEquals(n.Url, url)))
                return;
            string id = "url:" + url;
            _nodes[id] = new Node
            {
                Id = id,
                Url = url,
                Local = false,
                Healthy = true,
                Capacity = Math.Max(1, capacity > 0 ? capacity : 1),
                CapacityFromConfig = capacity > 0,
                Configured = true,
                OcrMode = "",
                Model = "",
                Dpi = 0,
            };
        }
    }

    /// <summary>Returns a warning when the remote model / mode / dpi does not match this node.</summary>
    public string? Register(ClusterRegisterRequest req)
    {
        string id = (req.NodeId ?? "").Trim();
        string url = (req.BaseUrl ?? "").Trim().TrimEnd('/');
        if (id.Length == 0)
            throw new ArgumentException("nodeId is required.");

        string? warning = Mismatch(req.OcrMode, req.Model, req.Dpi);
        lock (_gate)
        {
            HidePlaceholders(url, id);
            if (!_nodes.TryGetValue(id, out Node? node))
            {
                node = new Node { Id = id };
                _nodes[id] = node;
            }

            node.Url = url;
            node.Local = false;
            node.Hidden = false;
            node.Healthy = true;
            node.Registered = true;
            node.ProbeFails = 0;
            node.LastHeartbeat = DateTimeOffset.UtcNow;
            node.OcrMode = req.OcrMode ?? "";
            node.Model = req.Model ?? "";
            node.Dpi = req.Dpi;
            node.EngineCount = req.EngineCount;
            if (!node.CapacityFromConfig)
                node.Capacity = Math.Max(1, req.Capacity > 0 ? req.Capacity : 1);
            else if (req.Capacity > 0 && node.Capacity <= 0)
                node.Capacity = req.Capacity;
            node.Warning = warning;
        }

        return warning;
    }

    public void Heartbeat(ClusterHeartbeatRequest req)
    {
        string id = (req.NodeId ?? "").Trim();
        if (id.Length == 0)
            return;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(id, out Node? node) || node.Local)
                return;
            node.LastHeartbeat = DateTimeOffset.UtcNow;
            node.Healthy = req.Healthy;
            node.ProbeFails = 0;
            node.ReportedInFlight = Math.Max(0, req.InFlight);
            if (!node.CapacityFromConfig && req.Capacity > 0)
                node.Capacity = req.Capacity;
        }
    }

    /// <summary>Apply a health probe. Returns true when the node just became unhealthy.</summary>
    public bool NoteProbe(string nodeId, bool ok, ClusterInfoResponse? info)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(nodeId, out Node? node) || node.Local)
                return false;

            bool heartbeatFresh = DateTimeOffset.UtcNow - node.LastHeartbeat <= TimeSpan.FromMilliseconds(_heartbeatFreshMs);
            if (ok)
            {
                node.ProbeFails = 0;
                node.Healthy = true;
                if (info is not null)
                {
                    if (!string.IsNullOrWhiteSpace(info.OcrMode))
                        node.OcrMode = info.OcrMode;
                    if (!string.IsNullOrWhiteSpace(info.Model))
                        node.Model = info.Model;
                    if (info.Dpi > 0)
                        node.Dpi = info.Dpi;
                    if (info.EngineCount > 0)
                        node.EngineCount = info.EngineCount;
                    if (!node.CapacityFromConfig && info.Capacity > 0)
                        node.Capacity = info.Capacity;
                    node.Warning = Mismatch(node.OcrMode, node.Model, node.Dpi);
                }

                return false;
            }

            node.ProbeFails++;
            if (heartbeatFresh)
                return false;
            if (node.ProbeFails >= 2 && node.Healthy)
            {
                node.Healthy = false;
                return true;
            }

            return false;
        }
    }

    public void AddPages(IReadOnlyList<ClusterNodeLoad> nodes)
    {
        lock (_gate)
        {
            foreach (ClusterNodeLoad load in nodes)
            {
                if (load.PagesCommitted <= 0)
                    continue;
                if (!_nodes.TryGetValue(load.NodeId, out Node? node))
                {
                    node = new Node
                    {
                        Id = load.NodeId,
                        Healthy = true,
                        Capacity = 1,
                    };
                    _nodes[load.NodeId] = node;
                }

                node.PagesDone += load.PagesCommitted;
            }
        }
    }

    public List<ClusterRemote> Remotes()
    {
        lock (_gate)
        {
            List<ClusterRemote> list = [];
            foreach (Node node in _nodes.Values)
            {
                if (node.Local || node.Hidden || string.IsNullOrWhiteSpace(node.Url))
                    continue;
                list.Add(new ClusterRemote(node.Id, node.Url!, Math.Max(1, node.Capacity), node.Healthy, node.OcrMode, node.Model, node.Dpi, node.Warning));
            }

            return list;
        }
    }

    /// <summary>Mark nodes active in this Nacos poll cycle; prune those not seen recently.</summary>
    public void PruneStaleNacosNodes(TimeSpan maxAge, IReadOnlySet<string> activeNodeIds)
    {
        lock (_gate)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - maxAge;
            List<string> toRemove = [];
            foreach ((string id, Node node) in _nodes)
            {
                if (node.Local || node.Hidden)
                    continue;
                // Keep nodes from static config (Configured) and nodes that are still active in Nacos.
                if (node.Configured)
                    continue;
                if (activeNodeIds.Contains(id) && node.LastNacosSeen > cutoff)
                    continue;
                toRemove.Add(id);
            }

            foreach (string id in toRemove)
            {
                _nodes.Remove(id);
                _logger.LogDebug("Nacos node {NodeId} pruned (age > {MaxAge})", id, maxAge);
            }
        }
    }

    /// <summary>Record that a node was seen in the current Nacos poll.</summary>
    public void MarkNacosSeen(string nodeId)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(nodeId, out Node? node) && !node.Local)
                node.LastNacosSeen = DateTimeOffset.UtcNow;
        }
    }

    public ClusterHealthInfo BuildHealth(
        IReadOnlyList<ClusterScheduleSnapshot> active,
        ClusterLastJobHealth? lastJob)
    {
        lock (_gate)
        {
            var inflight = new Dictionary<string, int>(StringComparer.Ordinal);
            var activePages = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ClusterScheduleSnapshot snap in active)
            {
                foreach (ClusterNodeLoad load in snap.Nodes)
                {
                    inflight[load.NodeId] = inflight.GetValueOrDefault(load.NodeId) + load.InFlight;
                    activePages[load.NodeId] = activePages.GetValueOrDefault(load.NodeId) + load.PagesCommitted;
                }
            }

            List<ClusterNodeHealth> nodes = [];
            foreach (Node node in _nodes.Values)
            {
                if (node.Hidden)
                    continue;
                nodes.Add(new ClusterNodeHealth
                {
                    NodeId = node.Id,
                    Url = node.Url,
                    Local = node.Local,
                    Healthy = node.Healthy,
                    Capacity = node.Capacity,
                    InFlight = inflight.GetValueOrDefault(node.Id),
                    PagesDone = node.PagesDone + activePages.GetValueOrDefault(node.Id),
                    OcrMode = node.OcrMode,
                    Model = node.Model,
                    Dpi = node.Dpi,
                    Warning = node.Warning,
                });
            }

            nodes.Sort(static (a, b) =>
            {
                int local = b.Local.CompareTo(a.Local);
                return local != 0 ? local : string.CompareOrdinal(a.NodeId, b.NodeId);
            });

            return new ClusterHealthInfo
            {
                Enabled = true,
                Role = _self.Role,
                NodeId = _self.NodeId,
                AdvertiseUrl = _self.AdvertiseUrl,
                OcrMode = _self.OcrMode,
                Model = _self.Model,
                Dpi = _self.Dpi,
                Capacity = _self.Capacity,
                TokenSet = true,
                Nodes = nodes,
                LastJob = lastJob,
            };
        }
    }

    public static string? Mismatch(string? mode, string? model, int dpi, string ourMode, string ourModel, int ourDpi)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(mode) &&
            !string.IsNullOrWhiteSpace(ourMode) &&
            !string.Equals(mode.Trim(), ourMode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"ocr mode {mode} != {ourMode}");
        }

        if (!string.IsNullOrWhiteSpace(model) &&
            !string.IsNullOrWhiteSpace(ourModel) &&
            !string.Equals(model.Trim(), ourModel.Trim(), StringComparison.Ordinal))
        {
            parts.Add($"model {model} != {ourModel}");
        }

        if (dpi > 0 && ourDpi > 0 && dpi != ourDpi)
            parts.Add($"configured dpi {dpi} != {ourDpi}");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private string? Mismatch(string? mode, string? model, int dpi) =>
        Mismatch(mode, model, dpi, _self.OcrMode, _self.Model, _self.Dpi);

    private void HidePlaceholders(string url, string keepId)
    {
        foreach (Node node in _nodes.Values)
        {
            if (node.Local || node.Id == keepId)
                continue;
            if (!node.Registered && UrlEquals(node.Url, url))
                node.Hidden = true;
        }
    }

    private static bool UrlEquals(string? a, string? b) =>
        string.Equals(
            (a ?? "").TrimEnd('/'),
            (b ?? "").TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private sealed class Node
    {
        public string Id { get; set; } = "";
        public string? Url { get; set; }
        public bool Local { get; set; }
        public bool Healthy { get; set; }
        public bool Hidden { get; set; }
        public bool Registered { get; set; }
        public bool Configured { get; set; }
        public bool CapacityFromConfig { get; set; }
        public int Capacity { get; set; } = 1;
        public int EngineCount { get; set; }
        public int Dpi { get; set; }
        public int ProbeFails { get; set; }
        public int ReportedInFlight { get; set; }
        public int PagesDone { get; set; }
        public string OcrMode { get; set; } = "";
        public string Model { get; set; } = "";
        public string? Warning { get; set; }
        public DateTimeOffset LastHeartbeat { get; set; }
        public DateTimeOffset LastNacosSeen { get; set; }
    }
}

public sealed record ClusterRemote(
    string NodeId,
    string Url,
    int Capacity,
    bool Healthy,
    string OcrMode,
    string Model,
    int Dpi,
    string? Warning);
