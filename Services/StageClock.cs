using System.Runtime.CompilerServices;

namespace MiniOcr.Services;

/// <summary>
/// Allocation-free stage timestamps. Callers store <see cref="Stamp"/> values and add the
/// difference with <see cref="AddTicks"/>; conversion to milliseconds happens once, at a log line.
/// </summary>
internal static class StageClock
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Stamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double TicksToMs(long ticks) =>
        ticks <= 0 ? 0 : ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double MsSince(long start) =>
        start == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    public static void AddTicks(ref long ticks, long start)
    {
        if (start == 0)
            return;
        Interlocked.Add(ref ticks, Stamp() - start);
    }

    public static void NoteMin(ref long slot, long stamp)
    {
        long seen;
        do
        {
            seen = Volatile.Read(ref slot);
            if (seen != 0 && seen <= stamp)
                return;
        }
        while (Interlocked.CompareExchange(ref slot, stamp, seen) != seen);
    }

    public static void NoteMax(ref long slot, long stamp)
    {
        long seen;
        do
        {
            seen = Volatile.Read(ref slot);
            if (seen >= stamp)
                return;
        }
        while (Interlocked.CompareExchange(ref slot, stamp, seen) != seen);
    }

    public static void RaisePeak(ref int peak, int now)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref peak);
            if (now <= seen)
                return;
        }
        while (Interlocked.CompareExchange(ref peak, now, seen) != seen);
    }
}

/// <summary>
/// Local LLM NER timing. <see cref="WallMs"/> is the span from the first request start to the
/// last response. <see cref="RequestMs"/> is the sum of those request durations, so it grows
/// with concurrency and can exceed <see cref="WallMs"/>. <see cref="Peak"/> is the most
/// requests in flight at once.
/// </summary>
internal readonly record struct NerStages(double WallMs, double RequestMs, int Groups, int Peak);
