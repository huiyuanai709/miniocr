namespace MiniOcr.Services;

/// <summary>
/// Reads prepared seal images with a caller-supplied SimdPaddleOCR call and keeps the
/// orientation that looks most like a legend. Stops once a reading has a full name.
/// </summary>
public static class SealOcrPass
{
    public static async Task<string> ReadAsync(
        IReadOnlyList<SealRingOcr.SealCapture> captures,
        Func<SealRingOcr.SealImage, CancellationToken, Task<string>> recognize,
        CancellationToken ct)
    {
        if (captures.Count == 0)
            return "";

        List<string> lines = new(captures.Count);
        foreach (SealRingOcr.SealCapture capture in captures)
        {
            ct.ThrowIfCancellationRequested();
            string best = await ReadOneAsync(capture, recognize, ct).ConfigureAwait(false);
            if (best.Length > 0)
                lines.Add(best);
        }

        return string.Join('\n', lines);
    }

    private static async Task<string> ReadOneAsync(
        SealRingOcr.SealCapture capture,
        Func<SealRingOcr.SealImage, CancellationToken, Task<string>> recognize,
        CancellationToken ct)
    {
        string primary = await RecognizeAsync(capture.Primary, recognize, ct).ConfigureAwait(false);
        if (SealRingOcr.Enough(primary))
            return SealRingOcr.Prefer(primary);

        string reversed = await RecognizeAsync(capture.Reversed, recognize, ct).ConfigureAwait(false);
        string headsIn = "";
        string headsInReversed = "";
        if (!SealRingOcr.Enough(reversed))
        {
            headsIn = await RecognizeAsync(capture.HeadsIn, recognize, ct).ConfigureAwait(false);
            if (!SealRingOcr.Enough(headsIn))
                headsInReversed = await RecognizeAsync(capture.HeadsInReversed, recognize, ct).ConfigureAwait(false);
        }

        return SealRingOcr.Prefer(primary, reversed, headsIn, headsInReversed);
    }

    private static Task<string> RecognizeAsync(
        SealRingOcr.SealImage image,
        Func<SealRingOcr.SealImage, CancellationToken, Task<string>> recognize,
        CancellationToken ct)
    {
        if (image.IsEmpty)
            return Task.FromResult("");
        return recognize(image, ct);
    }
}
