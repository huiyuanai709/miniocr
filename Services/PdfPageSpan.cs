namespace MiniOcr.Services;

/// <summary>Detects a contiguous zero-based page span so callers can use a <see cref="Range"/>.</summary>
internal static class PdfPageSpan
{
    public static bool TryContiguous(IReadOnlyList<int> pages, out int first, out int count)
    {
        count = pages.Count;
        if (count == 0)
        {
            first = 0;
            return true;
        }

        first = pages[0];
        for (int i = 1; i < count; i++)
        {
            if (pages[i] != first + i)
                return false;
        }

        return first >= 0;
    }
}
