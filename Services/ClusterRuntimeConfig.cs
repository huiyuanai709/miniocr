using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Resolved <c>cluster</c> settings. Env <c>MINIOCR_CLUSTER_*</c> overrides the file.
/// Clustering stays off when the section is missing, <c>enabled</c> is false, or the token is empty.
/// </summary>
public sealed class ClusterRuntimeConfig
{
    public const string LocalModelName = "ChineseV6Tiny";

    public bool Enabled { get; init; }
    /// <summary><c>coordinator</c>, <c>worker</c>, or <c>both</c>.</summary>
    public string Role { get; init; } = "coordinator";
    public string NodeId { get; init; } = "";
    public string AdvertiseUrl { get; init; } = "";
    public string Token { get; init; } = "";
    public string CoordinatorUrl { get; init; } = "";
    public int Capacity { get; init; }
    public int PagesPerBatch { get; init; }
    public int LeaseFloorMs { get; init; } = 20_000;
    public int PageTimeoutMs { get; init; } = 20_000;
    public int HealthIntervalMs { get; init; } = 5_000;
    public int JobDeadlineMs { get; init; } = 300_000;
    public int JoinGraceMs { get; init; } = 500;
    public int SpeculativeTailPages { get; init; } = 4;
    /// <summary>Pages leased beyond OCR capacity so render stays ahead. 0 disables. Default 4.</summary>
    public int RenderAheadPages { get; init; } = 4;
    /// <summary>
    /// Copy a primary lease that has run past its expected remaining time once nothing
    /// is pending. Default on. <c>MINIOCR_CLUSTER_SPECULATIVE_STALE=0</c> disables it.
    /// </summary>
    public bool SpeculativeStaleLeases { get; init; } = true;
    /// <summary>
    /// Claim and render the next pages while OCR is still running, and post each page
    /// as it finishes. Default on. <c>MINIOCR_CLUSTER_PIPELINE_OCR=0</c> restores the
    /// claim-then-finish loop.
    /// </summary>
    public bool PipelineOcr { get; init; } = true;
    /// <summary>Promote routine dispatch logs (claim, heartbeat, batch done, empty poll) to Information.</summary>
    public bool VerboseDispatch { get; init; }
    /// <summary>
    /// When the cluster is on, workers that have an LLM key run text NER for groups they claim.
    /// Default true. <c>MINIOCR_CLUSTER_DISTRIBUTED_NER=0</c> keeps NER on the coordinator.
    /// </summary>
    public bool DistributedNer { get; init; } = true;
    /// <summary>Shared upload directory. Empty means workers always download.</summary>
    public string SharedDir { get; init; } = "";
    public IReadOnlyList<ClusterWorkerEndpoint> Workers { get; init; } = [];
    /// <summary>Set when a request to enable clustering was ignored (empty token).</summary>
    public string? DisabledReason { get; init; }

    /// <summary>Nacos configuration, if enabled. Null when Nacos is off or the nacos section is missing.</summary>
    public NacosFileConfig? Nacos { get; init; }

    /// <summary>True when Nacos-based service discovery should be used.</summary>
    public bool UseNacos => Enabled && Nacos is { Enabled: true };

    public bool IsCoordinator =>
        string.Equals(Role, "coordinator", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Role, "both", StringComparison.OrdinalIgnoreCase);

    public bool IsWorker =>
        string.Equals(Role, "worker", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Role, "both", StringComparison.OrdinalIgnoreCase);

    public static ClusterRuntimeConfig Resolve(AppConfigFile? file, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        ClusterFileConfig section = file?.Cluster ?? new ClusterFileConfig();

        bool enabled = section.Enabled;
        string? envEnabled = env("MINIOCR_CLUSTER_ENABLED");
        if (!string.IsNullOrWhiteSpace(envEnabled))
            enabled = ParseBool(envEnabled, enabled);

        string role = FirstNonEmpty(env("MINIOCR_CLUSTER_ROLE"), section.Role) ?? "coordinator";
        role = role.Trim().ToLowerInvariant();
        if (role is not ("coordinator" or "worker" or "both"))
            role = "coordinator";

        string token = FirstNonEmpty(env("MINIOCR_CLUSTER_TOKEN"), section.Token) ?? "";
        token = token.Trim();

        string? disabledReason = null;
        if (enabled && token.Length == 0)
        {
            enabled = false;
            disabledReason = "cluster.enabled is true but token is empty; clustering left off";
        }

        string nodeId = FirstNonEmpty(env("MINIOCR_CLUSTER_NODE_ID"), section.NodeId) ?? "";
        nodeId = nodeId.Trim();
        if (enabled && nodeId.Length == 0)
        {
            string host = Environment.MachineName;
            if (string.IsNullOrWhiteSpace(host))
                host = "node";
            nodeId = host + "-" + role + "-" + Guid.NewGuid().ToString("N")[..8];
        }

        string? advertise = TrimUrl(FirstNonEmpty(env("MINIOCR_CLUSTER_ADVERTISE_URL"), section.AdvertiseUrl));
        string? coordinator = TrimUrl(FirstNonEmpty(env("MINIOCR_CLUSTER_COORDINATOR_URL"), section.CoordinatorUrl));

        int capacity = ReadInt(env, "MINIOCR_CLUSTER_CAPACITY", section.Capacity ?? 0);
        if (capacity < 0)
            capacity = 0;

        int pagesPerBatch = ReadInt(env, "MINIOCR_CLUSTER_PAGES_PER_BATCH", section.PagesPerBatch ?? 0);
        pagesPerBatch = pagesPerBatch <= 0 ? 0 : Math.Clamp(pagesPerBatch, 1, 64);

        int leaseSeconds = Math.Clamp(
            ReadInt(env, "MINIOCR_CLUSTER_LEASE_SECONDS", section.LeaseSeconds <= 0 ? 20 : section.LeaseSeconds),
            1, 600);
        int pageTimeout = Math.Clamp(
            ReadInt(env, "MINIOCR_CLUSTER_PAGE_TIMEOUT_SECONDS", section.PageTimeoutSeconds <= 0 ? 20 : section.PageTimeoutSeconds),
            1, 600);
        int healthSeconds = Math.Clamp(
            ReadInt(env, "MINIOCR_CLUSTER_HEALTH_INTERVAL_SECONDS", section.HealthIntervalSeconds <= 0 ? 5 : section.HealthIntervalSeconds),
            1, 120);
        int deadlineSeconds = Math.Clamp(
            ReadInt(env, "MINIOCR_CLUSTER_JOB_DEADLINE_SECONDS", section.JobDeadlineSeconds <= 0 ? 300 : section.JobDeadlineSeconds),
            5, 3600);
        int joinGrace = Math.Clamp(
            ReadInt(env, "MINIOCR_CLUSTER_JOIN_GRACE_MS", section.JoinGraceMs <= 0 ? 500 : section.JoinGraceMs),
            0, 60_000);
        int tail = Math.Clamp(
            ReadInt(env, "MINIOCR_CLUSTER_SPECULATIVE_TAIL", section.SpeculativeTailPages <= 0 ? 4 : section.SpeculativeTailPages),
            1, 64);
        int renderAhead = Math.Clamp(
            ReadInt(env, "MINIOCR_CLUSTER_RENDER_AHEAD", section.RenderAheadPages),
            0, 16);
        bool staleLeases = section.SpeculativeStaleLeases ?? true;
        string? envStale = env("MINIOCR_CLUSTER_SPECULATIVE_STALE");
        if (!string.IsNullOrWhiteSpace(envStale))
            staleLeases = ParseBool(envStale, staleLeases);
        bool pipelineOcr = section.PipelineOcr ?? true;
        string? envPipeline = env("MINIOCR_CLUSTER_PIPELINE_OCR");
        if (!string.IsNullOrWhiteSpace(envPipeline))
            pipelineOcr = ParseBool(envPipeline, pipelineOcr);

        bool verboseDispatch = section.VerboseDispatch;
        string? envVerbose = env("MINIOCR_CLUSTER_VERBOSE_DISPATCH");
        if (!string.IsNullOrWhiteSpace(envVerbose))
            verboseDispatch = ParseBool(envVerbose, verboseDispatch);

        bool distributedNer = section.DistributedNer ?? true;
        string? envNer = env("MINIOCR_CLUSTER_DISTRIBUTED_NER");
        if (!string.IsNullOrWhiteSpace(envNer))
            distributedNer = ParseBool(envNer, distributedNer);

        string sharedDir = FirstNonEmpty(env("MINIOCR_CLUSTER_SHARED_DIR"), section.SharedDir) ?? "";
        sharedDir = sharedDir.Trim();

        // ---- Nacos ----
        NacosFileConfig? nacos = null;
        if (enabled)
        {
            nacos = ResolveNacos(file?.Nacos, env);
            if (nacos is not null && !nacos.Enabled)
                nacos = null;
        }

        List<ClusterWorkerEndpoint> workers = [];
        string? envWorkers = env("MINIOCR_CLUSTER_WORKERS");
        if (!string.IsNullOrWhiteSpace(envWorkers))
        {
            foreach (string part in envWorkers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string url = TrimUrl(part) ?? "";
                if (url.Length > 0)
                    workers.Add(new ClusterWorkerEndpoint(url, 0));
            }
        }
        else if (section.Workers is not null)
        {
            foreach (ClusterWorkerFileConfig w in section.Workers)
            {
                string url = TrimUrl(w.Url) ?? "";
                if (url.Length == 0)
                    continue;
                int cap = w.Capacity ?? 0;
                workers.Add(new ClusterWorkerEndpoint(url, Math.Max(0, cap)));
            }
        }

        return new ClusterRuntimeConfig
        {
            Enabled = enabled,
            Role = role,
            NodeId = nodeId,
            AdvertiseUrl = advertise ?? "",
            Token = token,
            CoordinatorUrl = coordinator ?? "",
            Capacity = capacity,
            PagesPerBatch = pagesPerBatch,
            LeaseFloorMs = leaseSeconds * 1000,
            PageTimeoutMs = pageTimeout * 1000,
            HealthIntervalMs = healthSeconds * 1000,
            JobDeadlineMs = deadlineSeconds * 1000,
            JoinGraceMs = joinGrace,
            SpeculativeTailPages = tail,
            RenderAheadPages = renderAhead,
            SpeculativeStaleLeases = staleLeases,
            PipelineOcr = pipelineOcr,
            VerboseDispatch = verboseDispatch,
            DistributedNer = distributedNer,
            SharedDir = sharedDir,
            Workers = workers,
            DisabledReason = disabledReason,
            Nacos = nacos,
        };
    }

    public int EffectiveCapacity(int engineCount, int visionConcurrency, bool llmMode)
    {
        if (Capacity > 0)
            return Math.Clamp(Capacity, 1, 64);
        if (llmMode)
            return Math.Clamp(Math.Max(1, visionConcurrency), 1, 64);
        return Math.Clamp(Math.Max(1, engineCount), 1, 64);
    }

    private static int ReadInt(Func<string, string?> env, string name, int fallback)
    {
        string? raw = env(name);
        return int.TryParse(raw, out int v) ? v : fallback;
    }

    private static bool ParseBool(string raw, bool fallback)
    {
        string s = raw.Trim();
        if (bool.TryParse(s, out bool b))
            return b;
        if (s is "1" or "yes" or "YES" or "on" or "ON")
            return true;
        if (s is "0" or "no" or "NO" or "off" or "OFF")
            return false;
        return fallback;
    }

    private static string? FirstNonEmpty(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a))
            return a.Trim();
        if (!string.IsNullOrWhiteSpace(b))
            return b.Trim();
        return null;
    }

    private static string? TrimUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        return url.Trim().TrimEnd('/');
    }

    private static NacosFileConfig? ResolveNacos(NacosFileConfig? file, Func<string, string?> env)
    {
        NacosFileConfig? section = file;
        bool enabled = section?.Enabled ?? false;
        string? envEnabled = env("MINIOCR_NACOS_ENABLED");
        if (!string.IsNullOrWhiteSpace(envEnabled))
            enabled = ParseBool(envEnabled, enabled);

        string serverAddr = FirstNonEmpty(env("MINIOCR_NACOS_SERVER_ADDR"), section?.ServerAddr) ?? "";
        serverAddr = (serverAddr ?? "").Trim().TrimEnd('/');
        if (!enabled || serverAddr.Length == 0)
            return null;

        return new NacosFileConfig
        {
            Enabled = true,
            ServerAddr = serverAddr,
            Namespace = FirstNonEmpty(env("MINIOCR_NACOS_NAMESPACE"), section?.Namespace) ?? "",
            ServiceName = FirstNonEmpty(env("MINIOCR_NACOS_SERVICE_NAME"), section?.ServiceName) ?? "miniocr-cluster",
            GroupName = FirstNonEmpty(env("MINIOCR_NACOS_GROUP"), section?.GroupName) ?? "DEFAULT_GROUP",
            ClusterName = FirstNonEmpty(env("MINIOCR_NACOS_CLUSTER_NAME"), section?.ClusterName) ?? "DEFAULT",
            Weight = ParseDouble(env("MINIOCR_NACOS_WEIGHT"), section?.Weight) ?? 1.0,
            AccessToken = FirstNonEmpty(env("MINIOCR_NACOS_ACCESS_TOKEN"), section?.AccessToken) ?? "",
            Username = FirstNonEmpty(env("MINIOCR_NACOS_USERNAME"), section?.Username) ?? "",
            Password = FirstNonEmpty(env("MINIOCR_NACOS_PASSWORD"), section?.Password) ?? "",
            HeartbeatIntervalSeconds = ReadInt(env, "MINIOCR_NACOS_HEARTBEAT_INTERVAL", section?.HeartbeatIntervalSeconds ?? 5),
            HealthyCheckSeconds = ParseDouble(env("MINIOCR_NACOS_HEALTHY_CHECK"), section?.HealthyCheckSeconds) ?? 6.0,
            Metadata = section?.Metadata,
        };
    }

    private static double? ParseDouble(string? raw, double? fallback)
    {
        if (double.TryParse(raw, out double v))
            return v;
        return fallback;
    }
}

public sealed record ClusterWorkerEndpoint(string Url, int Capacity);
