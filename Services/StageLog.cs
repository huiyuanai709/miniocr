using Microsoft.Extensions.Logging;

namespace MiniOcr.Services;

/// <summary>
/// Job-level stage lines. The source generator skips formatting when the level is disabled,
/// so Debug group lines do not allocate at the default Information level.
/// </summary>
internal static partial class StageLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OCR stages: downloadMs={DownloadMs} rasterizeMs={RasterizeMs} ocrMs={OcrMs} analyzeMs={AnalyzeMs} nerWallMs={NerWallMs} nerRequestMs={NerRequestMs} nerGroups={NerGroups} nerPeak={NerPeak} totalMs={TotalMs} gpuSubmitMs={GpuSubmitMs} gpuWaitMs={GpuWaitMs} cpuPreMs={CpuPreMs} cpuPostMs={CpuPostMs} gpuPages={GpuPages} fallbackPages={FallbackPages} gpuDevice={GpuDevice} initMs={InitMs} fence={Fence}")]
    public static partial void OcrStages(
        ILogger logger,
        double downloadMs,
        double rasterizeMs,
        double ocrMs,
        double analyzeMs,
        double nerWallMs,
        double nerRequestMs,
        int nerGroups,
        int nerPeak,
        double totalMs,
        double gpuSubmitMs,
        double gpuWaitMs,
        double cpuPreMs,
        double cpuPostMs,
        int gpuPages,
        int fallbackPages,
        string gpuDevice,
        double initMs,
        string fence);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LLM NER stages: groups={Groups} wallMs={WallMs} requestMs={RequestMs} peak={Peak}")]
    public static partial void NerSummary(ILogger logger, int groups, double wallMs, double requestMs, int peak);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM NER group pages={Pages} requestMs={RequestMs}")]
    public static partial void NerGroup(ILogger logger, string pages, double requestMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Parallel PDF render pool first lease ms={Ms}")]
    public static partial void RenderFirstLease(ILogger logger, double ms);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Cluster worker {NodeId} job {JobId} progress: localPages={Pages} jobPages={Total} {Rate:F1} pages/s downloadMs={DownloadMs:F0} renderMs={RenderMs:F0} ocrMs={OcrMs:F0} postMs={PostMs:F0} waitMs={WaitMs:F0}")]
    public static partial void WorkerProgress(
        ILogger logger,
        string nodeId,
        string jobId,
        int pages,
        int total,
        double rate,
        double downloadMs,
        double renderMs,
        double ocrMs,
        double postMs,
        double waitMs);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Cluster worker {NodeId} job {JobId} progress: localPages={Pages} jobPages={Total} {Rate:F1} pages/s downloadMs={DownloadMs:F0} renderMs={RenderMs:F0} ocrMs={OcrMs:F0} postMs={PostMs:F0} waitMs={WaitMs:F0} nerGroups={NerGroups}")]
    public static partial void WorkerProgressNer(
        ILogger logger,
        string nodeId,
        string jobId,
        int pages,
        int total,
        double rate,
        double downloadMs,
        double renderMs,
        double ocrMs,
        double postMs,
        double waitMs,
        int nerGroups);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Cluster worker {NodeId} left job {JobId} localPages={Pages} {Rate:F1} pages/s downloadMs={DownloadMs:F0} renderMs={RenderMs:F0} ocrMs={OcrMs:F0} postMs={PostMs:F0} waitMs={WaitMs:F0}")]
    public static partial void WorkerDone(
        ILogger logger,
        string nodeId,
        string jobId,
        int pages,
        double rate,
        double downloadMs,
        double renderMs,
        double ocrMs,
        double postMs,
        double waitMs);
}
