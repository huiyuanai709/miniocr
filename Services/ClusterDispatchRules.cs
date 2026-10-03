namespace MiniOcr.Services;

/// <summary>
/// Who may be offered a job. Workers already enforce a two-session cap when they are
/// notified; dispatch has to enforce the same cap, and it must not hand a finished OCR
/// job back to a node that cannot help with whatever NER is left.
/// </summary>
public static class ClusterDispatchRules
{
    public const int MaxWorkerSessions = 2;

    public static bool AtSessionCap(int activeSessions) => activeSessions >= MaxWorkerSessions;

    /// <summary>
    /// <paramref name="activeSessions"/> is the worker's live session count when it sent one.
    /// Older workers omit it; fall back to the number of job ids they listed.
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
