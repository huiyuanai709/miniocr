namespace MiniOcr.Services;

/// <summary>
/// Decides whether a failed worker-process spawn keeps <c>parallel</c> or switches
/// this process to <c>inprocess</c>. The log line is the startup message operators see.
/// </summary>
public static class ParallelStartupFallback
{
    public static string Message(Exception failure) =>
        "Parallel PDF render workers failed to start: " + failure.Message +
        ". Falling back to in-process rendering (ocr.renderMode=inprocess).";

    /// <summary>
    /// When <paramref name="spawnFailure"/> is set and the config asked for parallel,
    /// log <see cref="Message"/> and return in-process. A null failure leaves the config unchanged.
    /// </summary>
    public static OcrRuntimeConfig Apply(OcrRuntimeConfig config, Exception? spawnFailure, Action<string> log)
    {
        if (spawnFailure is null || !config.IsParallelRender)
            return config;

        log(Message(spawnFailure));
        return config.WithRenderMode("inprocess");
    }
}
