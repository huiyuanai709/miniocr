using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Copies page references for the trace queue. Strings are immutable, so this does not
/// copy OCR character data and does not run inside recognition.
/// </summary>
public static class OcrTracePages
{
    public static OcrTracePage[] Copy(IReadOnlyList<OcrPageResult>? pages)
    {
        if (pages is null || pages.Count == 0)
            return [];
        var copy = new OcrTracePage[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            OcrPageResult page = pages[i];
            copy[i] = new OcrTracePage(page.Page, page.Source ?? "", page.Text ?? "");
        }

        return copy;
    }
}
