namespace MiniOcr.Services;

/// <summary>Single-range <c>bytes=</c> slice of a PDF body. Multiple ranges are not sliced.</summary>
public static class ClusterPdfRange
{
    public static bool TrySlice(long length, long? from, long? to, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (length <= 0)
            return false;

        if (from is null && to is null)
            return false;

        if (from is null)
        {
            long suffix = to!.Value;
            if (suffix <= 0)
                return false;
            if (suffix > length)
                suffix = length;
            start = length - suffix;
            end = length - 1;
            return true;
        }

        start = from.Value;
        end = to ?? (length - 1);
        if (start < 0 || start >= length)
            return false;
        if (end < start)
            return false;
        if (end >= length)
            end = length - 1;
        return true;
    }
}
