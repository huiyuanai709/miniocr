namespace MiniOcr.Services;

/// <summary>
/// Runs PDFs one after another. File N+1 starts when <see cref="Started.ReleaseNext"/>
/// completes — the cluster signals that as soon as file N's claim queue is empty and a
/// GPU slot is free, otherwise when file N's OCR finishes. Each file's NER stays in
/// flight and is joined before <see cref="RunAsync"/> returns.
/// </summary>
public static class SequentialPdfOverlap
{
    /// <param name="OcrCompleted">
    /// Completes when this file's GPU OCR is finished or has failed. NER may still be running.
    /// </param>
    /// <param name="Finished">
    /// Completes when this file is fully done, including NER. Must not fault;
    /// the caller records per-file errors inside the task.
    /// </param>
    /// <param name="ReleaseNext">
    /// Completes when the next PDF may take a free GPU. Null waits for <paramref name="OcrCompleted"/>.
    /// </param>
    public readonly record struct Started(Task OcrCompleted, Task Finished, Task? ReleaseNext = null);

    public static async Task RunAsync(
        int fileCount,
        Func<int, CancellationToken, Task<Started?>> start,
        bool continueAfterFailure,
        CancellationToken ct)
    {
        if (fileCount <= 0)
            return;

        List<Task> finished = new(fileCount);
        try
        {
            for (int i = 0; i < fileCount; i++)
            {
                Started? started = await start(i, ct).ConfigureAwait(false);
                if (started is null)
                    continue;

                Started run = started.Value;
                // Keep the tail even when OCR faults, so NER (and buffer release) still finishes.
                finished.Add(run.Finished);
                Task release = run.ReleaseNext ?? run.OcrCompleted;
                if (run.ReleaseNext is not null)
                    _ = Observe(run.OcrCompleted);
                try
                {
                    await release.ConfigureAwait(false);
                }
                catch (Exception) when (continueAfterFailure)
                {
                    // This file's OCR failed and the GPU is free. The tail records the error.
                }
            }
        }
        finally
        {
            await Task.WhenAll(finished).ConfigureAwait(false);
        }
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The overlap gate already decided whether to start the next file.
            // The file's own tail records the OCR failure.
        }
    }
}
