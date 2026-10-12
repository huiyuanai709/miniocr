namespace MiniOcr.Services;

/// <summary>
/// Runs PDFs one GPU at a time. OCR of file N+1 starts when file N's OCR finishes.
/// Each file's NER (or any other post-OCR work) stays in flight and is joined
/// before <see cref="RunAsync"/> returns, so the combined result is complete.
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
    public readonly record struct Started(Task OcrCompleted, Task Finished);

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

                // Keep the tail even when OCR faults, so NER (and buffer release) still finishes.
                finished.Add(started.Value.Finished);
                try
                {
                    await started.Value.OcrCompleted.ConfigureAwait(false);
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
}
