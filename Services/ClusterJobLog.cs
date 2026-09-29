using Microsoft.Extensions.Logging;

namespace MiniOcr.Services;

/// <summary>
/// Log policy for the cluster dispatch path.
/// Routine claim / heartbeat / batch-done / empty-poll lines are Debug unless
/// <see cref="ClusterRuntimeConfig.VerboseDispatch"/> is set, in which case they are Information.
/// Either way, <c>Logging:LogLevel</c> on the caller category still filters them.
/// Progress summaries, speculative retries, and lease expiry stay Information.
/// </summary>
internal static class ClusterJobLog
{
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ProgressMinGap = TimeSpan.FromSeconds(1);
    public const int ProgressPercentStep = 10;

    internal readonly record struct ProgressDecision(bool Log, int Percent, int Bucket, double PagesPerSecond);

    public static ProgressDecision EvaluateProgress(
        int done,
        int total,
        DateTimeOffset started,
        int lastDone,
        int lastBucket,
        DateTimeOffset lastLoggedAt,
        DateTimeOffset now)
    {
        int percent = total <= 0 ? 0 : (int)((long)done * 100 / total);
        int bucket = ProgressPercentStep <= 0 ? percent : percent / ProgressPercentStep;
        double seconds = Math.Max(0.001, (now - started).TotalSeconds);
        double rate = done / seconds;
        if (done <= 0 || total <= 0 || done >= total || done == lastDone)
            return new ProgressDecision(false, percent, bucket, rate);

        DateTimeOffset baseline = lastLoggedAt == default ? started : lastLoggedAt;
        if (now - baseline < ProgressMinGap)
            return new ProgressDecision(false, percent, bucket, rate);

        bool timeDue = now - baseline >= ProgressInterval;
        bool bucketDue = bucket > lastBucket && percent >= ProgressPercentStep;
        return new ProgressDecision(timeDue || bucketDue, percent, bucket, rate);
    }

    public static string FormatByNode(ClusterScheduleSnapshot snap)
    {
        if (snap.Nodes.Count == 0)
            return "(none)";
        List<string> parts = [];
        foreach (ClusterNodeLoad load in snap.Nodes.OrderBy(n => n.NodeId, StringComparer.Ordinal))
        {
            if (load.PagesCommitted <= 0)
                continue;
            parts.Add(load.NodeId + "=" + load.PagesCommitted);
        }

        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    public static string FormatNer(ClusterNerSnapshot snap)
    {
        string by = FormatByNode(new ClusterScheduleSnapshot { Nodes = snap.Nodes });
        return "ner=" + snap.Done + "/" + snap.Formed + " nerInFlight=" + snap.InFlight + " nerByNode=" + by;
    }

    public static void Claim(
        ILogger logger,
        bool verbose,
        string jobId,
        string nodeId,
        string batchId,
        string pages,
        bool speculative,
        int leaseMs)
    {
        if (speculative)
        {
            logger.LogInformation(
                "Cluster job {JobId} speculative retry node={Node} batch={Batch} pages={Pages} leaseMs={Lease}",
                jobId,
                nodeId,
                batchId,
                pages,
                leaseMs);
            return;
        }

        if (verbose)
        {
            logger.LogInformation(
                "Cluster job {JobId} claim node={Node} batch={Batch} pages={Pages} speculative={Spec} leaseMs={Lease}",
                jobId,
                nodeId,
                batchId,
                pages,
                false,
                leaseMs);
        }
        else
        {
            logger.LogDebug(
                "Cluster job {JobId} claim node={Node} batch={Batch} pages={Pages} speculative={Spec} leaseMs={Lease}",
                jobId,
                nodeId,
                batchId,
                pages,
                false,
                leaseMs);
        }
    }

    public static void NerClaim(
        ILogger logger,
        bool verbose,
        string jobId,
        string nodeId,
        string groupId,
        string pages,
        int leaseMs)
    {
        if (verbose)
        {
            logger.LogInformation(
                "Cluster job {JobId} NER claim node={Node} group={Group} pages={Pages} leaseMs={Lease}",
                jobId,
                nodeId,
                groupId,
                pages,
                leaseMs);
        }
        else
        {
            logger.LogDebug(
                "Cluster job {JobId} NER claim node={Node} group={Group} pages={Pages} leaseMs={Lease}",
                jobId,
                nodeId,
                groupId,
                pages,
                leaseMs);
        }
    }

    public static void NerDone(ILogger logger, bool verbose, string nodeId, string jobId, string groupId, int entities)
    {
        if (verbose)
        {
            logger.LogInformation(
                "Cluster worker {NodeId} job {JobId} NER group {Group} done entities={Entities}",
                nodeId,
                jobId,
                groupId,
                entities);
        }
        else
        {
            logger.LogDebug(
                "Cluster worker {NodeId} job {JobId} NER group {Group} done entities={Entities}",
                nodeId,
                jobId,
                groupId,
                entities);
        }
    }

    public static void BatchDone(ILogger logger, bool verbose, string nodeId, string jobId, string batchId, int pages)
    {
        if (verbose)
        {
            logger.LogInformation(
                "Cluster worker {NodeId} job {JobId} batch {Batch} done pages={Pages}",
                nodeId,
                jobId,
                batchId,
                pages);
        }
        else
        {
            logger.LogDebug(
                "Cluster worker {NodeId} job {JobId} batch {Batch} done pages={Pages}",
                nodeId,
                jobId,
                batchId,
                pages);
        }
    }

    public static void Heartbeat(ILogger logger, bool verbose, string nodeId, int inFlight, int capacity)
    {
        if (verbose)
        {
            logger.LogInformation(
                "Cluster heartbeat node={Node} inFlight={InFlight} capacity={Capacity}",
                nodeId,
                inFlight,
                capacity);
        }
        else
        {
            logger.LogDebug(
                "Cluster heartbeat node={Node} inFlight={InFlight} capacity={Capacity}",
                nodeId,
                inFlight,
                capacity);
        }
    }

    public static void DispatchWait(ILogger logger, bool verbose, string nodeId)
    {
        if (verbose)
            logger.LogInformation("Cluster dispatch poll empty node={Node}", nodeId);
        else
            logger.LogDebug("Cluster dispatch poll empty node={Node}", nodeId);
    }

    public static void ClaimWait(ILogger logger, bool verbose, string nodeId, string jobId)
    {
        if (verbose)
            logger.LogInformation("Cluster claim poll empty node={Node} job={JobId}", nodeId, jobId);
        else
            logger.LogDebug("Cluster claim poll empty node={Node} job={JobId}", nodeId, jobId);
    }

    public static void LeaseExpired(
        ILogger logger,
        string jobId,
        string nodeId,
        string batchId,
        string pages,
        bool speculative)
    {
        logger.LogInformation(
            "Cluster job {JobId} lease expired node={Node} batch={Batch} requeued=[{Pages}] speculative={Spec}",
            jobId,
            nodeId,
            batchId,
            pages,
            speculative);
    }

    public static void Progress(
        ILogger logger,
        string jobId,
        int done,
        int total,
        int percent,
        double pagesPerSecond,
        int inFlight,
        string byNode,
        string? ner = null)
    {
        if (string.IsNullOrEmpty(ner))
        {
            logger.LogInformation(
                "Cluster job {JobId} progress: {Done}/{Total} ({Percent}%) {Rate:F1} pages/s inFlight={InFlight} byNode={ByNode}",
                jobId,
                done,
                total,
                percent,
                pagesPerSecond,
                inFlight,
                byNode);
            return;
        }

        logger.LogInformation(
            "Cluster job {JobId} progress: {Done}/{Total} ({Percent}%) {Rate:F1} pages/s inFlight={InFlight} byNode={ByNode} {Ner}",
            jobId,
            done,
            total,
            percent,
            pagesPerSecond,
            inFlight,
            byNode,
            ner);
    }
}
