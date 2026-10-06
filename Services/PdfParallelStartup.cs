using PDFtoImage.Parallel;

namespace MiniOcr.Services;

/// <summary>
/// Starts the PDFtoImage.Parallel workers before the web host serves traffic.
/// Spawn failures switch this process to in-process rendering and leave a single log line.
/// </summary>
public static class PdfParallelStartup
{
    public static async Task<(OcrRuntimeConfig Config, ParallelPdfProcessor? Processor)> ProbeAsync(
        OcrRuntimeConfig config,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!config.IsParallelRender)
            return (config, null);

        ParallelPdfProcessor? processor = null;
        try
        {
            long started = StageClock.Stamp();
            processor = new ParallelPdfProcessor(PdfParallelOptions.CreateProcessor(config.RenderProcessCount));
            await processor.PrewarmAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Parallel PDF render workers started (processes={Processes}, coldStartMs={ColdStartMs:F0})",
                config.RenderProcessCount,
                StageClock.MsSince(started));
            return (config, processor);
        }
        catch (Exception ex)
        {
            if (processor is not null)
            {
                try
                {
                    processor.Dispose();
                }
                catch (Exception disposeEx)
                {
                    ex = new AggregateException(ex, disposeEx);
                }
            }

            OcrRuntimeConfig fallback = ParallelStartupFallback.Apply(config, ex, message =>
            {
                Console.Error.WriteLine(message);
                logger.LogWarning(ex, "{Message}", message);
            });
            return (fallback, null);
        }
    }
}
