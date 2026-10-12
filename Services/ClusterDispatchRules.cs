namespace MiniOcr.Services;

/// <summary>
/// Who may be offered a job. Workers enforce a two-session cap on OCR only.
/// A file whose OCR is finished keeps NER running, but that tail must not occupy
/// a slot the next PDF's GPU work needs. Dispatch still will not hand a finished
/// OCR job back to a node that cannot help with whatever NER is left.
/// </summary>
public static class ClusterDispatchRules
{
    public const int MaxWorkerSessions = 2;

    public static bool AtSessionCap(int activeSessions) => activeSessions >= MaxWorkerSessions;

    /// <summary>
    /// Lower sorts first. An open OCR job outranks an older job that only has NER left,
    /// so name extraction on a finished PDF does not take the GPU the next file needs.
    /// </summary>
    public static int OfferOrder(bool ocrComplete) => ocrComplete ? 1 : 0;

    /// <summary>
    /// <paramref name="ocrSessions"/> is jobs whose OCR claim loop is still running.
    /// NER-only tails are not included. The worker lists those job ids as active so
    /// the same job is not offered again, without spending an OCR slot on them.
    /// </summary>
    public static bool CanStartOcrSession(int ocrSessions, bool alreadyInJob) =>
        alreadyInJob || ocrSessions < MaxWorkerSessions;

    /// <summary>
    /// <paramref name="activeSessions"/> is the worker's OCR session count when it sent one.
    /// NER-only tails are not included. Older workers omit it; fall back to the number of job ids they listed.
    /// </summary>
    public static int SessionCount(int? reportedSessions, int listedJobs) =>
        reportedSessions is int n && n >= 0 ? n : Math.Max(0, listedJobs);

    /// <summary>
    /// OCR still open: the node can take pages. OCR finished: only a node that can run NER,
    /// and only while a group is still outstanding.
    /// </summary>
    public static bool ShouldOfferJob(bool ocrComplete, bool nerPending, bool nodeCanNer)
    {
        if (!ocrComplete)
            return true;
        return nodeCanNer && nerPending;
    }
}
