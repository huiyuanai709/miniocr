namespace MiniOcr.Models;

/// <summary>
/// <c>config.json</c> <c>cluster</c> section. Absent or <c>enabled: false</c> → single-node OCR.
/// </summary>
public sealed class ClusterFileConfig
{
    public bool Enabled { get; set; }
    /// <summary><c>coordinator</c>, <c>worker</c>, or <c>both</c>.</summary>
    public string Role { get; set; } = "coordinator";
    public string NodeId { get; set; } = "";
    /// <summary>URL other nodes use to reach this process (no trailing path).</summary>
    public string AdvertiseUrl { get; set; } = "";
    /// <summary>Shared bearer secret. Never logged. Required when enabled.</summary>
    public string Token { get; set; } = "";
    /// <summary>Worker: coordinator base URL to register with and pull work from.</summary>
    public string CoordinatorUrl { get; set; } = "";
    /// <summary>0 / null = this node's engine count (llm mode: vision concurrency).</summary>
    public int? Capacity { get; set; }
    /// <summary>0 / null = auto from capacity, clamped to 1–16, shrunk on the tail.</summary>
    public int? PagesPerBatch { get; set; }
    /// <summary>Minimum lease for a claimed batch. Floor for one page; longer batches add <see cref="PageTimeoutSeconds"/>.</summary>
    public int LeaseSeconds { get; set; } = 20;
    /// <summary>Extra lease seconds per page in a batch.</summary>
    public int PageTimeoutSeconds { get; set; } = 20;
    public int HealthIntervalSeconds { get; set; } = 5;
    /// <summary>Backstop: after this, remote leases are dropped and the coordinator finishes leftovers locally.</summary>
    public int JobDeadlineSeconds { get; set; } = 300;
    /// <summary>
    /// While remotes are expected, the coordinator holds itself to one local window
    /// until this many milliseconds pass or the grace elapses. Stops a fast coordinator
    /// from finishing a tiny PDF before workers download it. Big jobs are unaffected
    /// once the window is full and the grace (default 500ms) has passed.
    /// </summary>
    public int JoinGraceMs { get; set; } = 500;
    /// <summary>When this many pages are still leased and nothing is pending, idle nodes may copy the tail.</summary>
    public int SpeculativeTailPages { get; set; } = 4;
    /// <summary>
    /// Pages a node may lease beyond its OCR capacity so the next batch renders while engines are busy.
    /// 0 disables. Default 4.
    /// </summary>
    public int RenderAheadPages { get; set; } = 4;
    /// <summary>
    /// When null, a stale primary lease can be copied once the pending queue is empty,
    /// even if more than <see cref="SpeculativeTailPages"/> pages are still out.
    /// Set false to keep only the small-tail copy.
    /// </summary>
    public bool? SpeculativeStaleLeases { get; set; }
    /// <summary>
    /// When null, a node claims the next pages while OCR is still running and posts each page as it finishes.
    /// Set false to claim a batch, finish it, post it, then claim again.
    /// </summary>
    public bool? PipelineOcr { get; set; }
    /// <summary>
    /// When true, per-claim / heartbeat / batch-done / empty-poll lines are Information.
    /// Default false: those lines are Debug. Progress summaries stay Information either way.
    /// <c>Logging:LogLevel</c> for the cluster categories still applies.
    /// </summary>
    public bool VerboseDispatch { get; set; }
    /// <summary>
    /// When null, distributed NER is on whenever the cluster is enabled.
    /// Workers with an LLM key extract names for groups they claim; the coordinator merges them.
    /// Set false to keep text NER on the coordinator only.
    /// </summary>
    public bool? DistributedNer { get; set; }
    public List<ClusterWorkerFileConfig>? Workers { get; set; }
}

public sealed class ClusterWorkerFileConfig
{
    public string Url { get; set; } = "";
    /// <summary>0 / null = discover from the worker's <c>/cluster/info</c>.</summary>
    public int? Capacity { get; set; }
}

public sealed class ClusterHealthInfo
{
    public bool Enabled { get; set; }
    public string Role { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string AdvertiseUrl { get; set; } = "";
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    public int Capacity { get; set; }
    public bool TokenSet { get; set; }
    /// <summary>Text NER runs on workers that have an LLM key. Default on when the cluster is enabled.</summary>
    public bool DistributedNer { get; set; }
    public List<ClusterNodeHealth> Nodes { get; set; } = [];
    public ClusterLastJobHealth? LastJob { get; set; }
}

public sealed class ClusterNodeHealth
{
    public string NodeId { get; set; } = "";
    public string? Url { get; set; }
    public bool Local { get; set; }
    public bool Healthy { get; set; }
    public int Capacity { get; set; }
    public int InFlight { get; set; }
    public int PagesDone { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    /// <summary>This node has <c>llm.enabled</c> and an API key, so it can run text NER.</summary>
    public bool LlmConfigured { get; set; }
    /// <summary>This node's <c>llm.maxConcurrency</c>. Zero when no key is configured.</summary>
    public int NerConcurrency { get; set; }
    public string? Warning { get; set; }
}

public sealed class ClusterLastJobHealth
{
    public string JobId { get; set; } = "";
    public int PageCount { get; set; }
    public double ElapsedMs { get; set; }
    public string PageTextSha256 { get; set; } = "";
    public int NerGroups { get; set; }
    public List<ClusterNodePages> Nodes { get; set; } = [];
    /// <summary>NER groups completed by each node. <see cref="ClusterNodePages.Pages"/> is a group count.</summary>
    public List<ClusterNodePages>? NerByNode { get; set; }
    /// <summary>Milliseconds from job start until every OCR page was committed. Zero if the job stopped first.</summary>
    public double OcrDoneMs { get; set; }
    /// <summary>Milliseconds from job start until distributed NER finished. Zero when NER was not distributed.</summary>
    public double NerDoneMs { get; set; }
    /// <summary>Pages handed out as speculative copies.</summary>
    public int SpeculativeCopies { get; set; }
    /// <summary>NER groups given up after retries or dropped at the deadline.</summary>
    public int AbandonedNerGroups { get; set; }
}

public sealed class ClusterNodePages
{
    public string NodeId { get; set; } = "";
    public int Pages { get; set; }
}

public sealed class ClusterRegisterRequest
{
    public string NodeId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int Capacity { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    public int EngineCount { get; set; }
    /// <summary>Null when the caller does not know. False means OCR only; the coordinator runs NER for its pages.</summary>
    public bool? LlmConfigured { get; set; }
    public int? NerConcurrency { get; set; }
}

public sealed class ClusterRegisterResponse
{
    public bool Ok { get; set; }
    public string? Warning { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
}

public sealed class ClusterHeartbeatRequest
{
    public string NodeId { get; set; } = "";
    public int Capacity { get; set; }
    public int InFlight { get; set; }
    public bool Healthy { get; set; } = true;
    public bool? LlmConfigured { get; set; }
    public int? NerConcurrency { get; set; }
}

public sealed class ClusterInfoResponse
{
    public string NodeId { get; set; } = "";
    public string Role { get; set; } = "";
    public int Capacity { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    public int EngineCount { get; set; }
    public bool Healthy { get; set; } = true;
    public int ActiveSessions { get; set; }
    public int PagesDone { get; set; }
    public bool LlmConfigured { get; set; }
    public int NerConcurrency { get; set; }
}

public sealed class ClusterNotifyRequest
{
    public string JobId { get; set; } = "";
    public string CoordinatorUrl { get; set; } = "";
    public int Dpi { get; set; }
    public int PageCount { get; set; }
    /// <summary>Original PDF URL. Workers prefer this over the coordinator copy when it is set.</summary>
    public string? SourceUrl { get; set; }
    /// <summary>
    /// Download <see cref="SourceUrl"/> into the worker cache only. No session and no page lease.
    /// Sent as soon as the coordinator knows the link, before its own download finishes.
    /// </summary>
    public bool Prefetch { get; set; }
}

public sealed class ClusterDispatchRequest
{
    public string NodeId { get; set; } = "";
    public int Capacity { get; set; }
    /// <summary>
    /// Jobs this worker is already in, plus ones it recently left. The coordinator will not offer those ids.
    /// Recently-left ids stop a just-finished session from being handed straight back.
    /// </summary>
    public List<string>? ActiveJobs { get; set; }
    /// <summary>
    /// Live session count, not including recently-left jobs. Null means the caller is an older worker
    /// and <see cref="ActiveJobs"/> is the session list.
    /// </summary>
    public int? ActiveSessions { get; set; }
}

public sealed class ClusterDispatchResponse
{
    public bool Wait { get; set; }
    public int RetryAfterMs { get; set; } = 300;
    public string? JobId { get; set; }
    public int Dpi { get; set; }
    public int PageCount { get; set; }
    /// <summary>Absolute path on the coordinator, e.g. <c>/cluster/jobs/{id}/pdf</c>.</summary>
    public string? PdfPath { get; set; }
    /// <summary>Original PDF URL when the coordinator still has it. Empty when the job was a local file.</summary>
    public string? SourceUrl { get; set; }
}

public sealed class ClusterJoinRequest
{
    public string NodeId { get; set; } = "";
    public int Capacity { get; set; }
    /// <summary>True while this node is still fetching the PDF. A later join with false means it can claim pages.</summary>
    public bool Downloading { get; set; }
}

public sealed class ClusterClaimRequest
{
    public string NodeId { get; set; } = "";
    public int MaxPages { get; set; }
}

public sealed class ClusterClaimResponse
{
    public bool Done { get; set; }
    public bool Wait { get; set; }
    public string? BatchId { get; set; }
    /// <summary>1-based page numbers.</summary>
    public List<int>? Pages { get; set; }
    public int LeaseMs { get; set; }
    public int RetryAfterMs { get; set; }
    public bool Speculative { get; set; }
}

public sealed class ClusterResultRequest
{
    public string NodeId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public List<OcrPageResult>? Pages { get; set; }
}

public sealed class ClusterFailRequest
{
    public string NodeId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class ClusterAck
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Accepted { get; set; }
}

public sealed class ClusterNerClaimRequest
{
    public string NodeId { get; set; } = "";
    public bool LlmConfigured { get; set; }
    public int NerConcurrency { get; set; }
    /// <summary>How long the coordinator may hold this call waiting for a group. Zero uses the server default.</summary>
    public int WaitMs { get; set; }
}

public sealed class ClusterNerPageText
{
    public int Page { get; set; }
    public string Text { get; set; } = "";
}

public sealed class ClusterNerClaimResponse
{
    public bool Done { get; set; }
    public bool Wait { get; set; }
    public string? GroupId { get; set; }
    public List<int>? Pages { get; set; }
    public List<ClusterNerPageText>? PageTexts { get; set; }
    /// <summary>Group text the model sees, before the next-page head is appended.</summary>
    public string? PromptText { get; set; }
    public string? Lookahead { get; set; }
    public int LookaheadPage { get; set; }
    public int LeaseMs { get; set; }
    public int RetryAfterMs { get; set; }
    /// <summary>Scheduler version observed with this response. Long-poll waits until it changes.</summary>
    public long Version { get; set; }
}

public sealed class ClusterNerEntityHit
{
    /// <summary><c>person</c> (B04) or <c>company</c> (B06).</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public int Page { get; set; }
    public int Count { get; set; }
    public List<string> OriginText { get; set; } = [];
}

public sealed class ClusterNerResultRequest
{
    public string NodeId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public List<string>? Companies { get; set; }
    public List<string>? Persons { get; set; }
    public List<ClusterNerEntityHit>? Entities { get; set; }
}

// ---- Nacos Open API DTOs ----

/// <summary>Nacos instance registration body (POST /nacos/v1/ns/instance).</summary>
public sealed class NacosInstanceRegister
{
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string ServiceName { get; set; } = "";
    public string NamespaceId { get; set; } = "";
    public double Weight { get; set; } = 1.0;
    public bool Enabled { get; set; } = true;
    public bool Healthy { get; set; } = true;
    public string? ClusterName { get; set; }
    public string? Metadata { get; set; }
    public string? GroupName { get; set; }
    public bool Ephemeral { get; set; } = true;
}

/// <summary>Nacos heartbeat body (PUT /nacos/v1/ns/instance/beat).</summary>
public sealed class NacosHeartbeatRequest
{
    public string ServiceName { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string NamespaceId { get; set; } = "";
    public string? GroupName { get; set; }
    public string? ClusterName { get; set; }
    public bool Ephemeral { get; set; } = true;
    public string? Metadata { get; set; }
}

/// <summary>Nacos instance list response (GET /nacos/v1/ns/instance/list).</summary>
public sealed class NacosInstanceListResponse
{
    public string? Dom { get; set; }
    public string? Name { get; set; }
    public List<NacosInstance>? Hosts { get; set; }
}

/// <summary>A single instance in the Nacos instance list response.</summary>
public sealed class NacosInstance
{
    public string? InstanceId { get; set; }
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public double Weight { get; set; } = 1.0;
    public bool Healthy { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool Ephemeral { get; set; } = true;
    public string? ClusterName { get; set; }
    public string? ServiceName { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>Nacos login response (POST /nacos/v1/auth/login).</summary>
public sealed class NacosLoginResponse
{
    public string? AccessToken { get; set; }
    public int TokenTtl { get; set; }
    public bool GlobalAdmin { get; set; }
}
