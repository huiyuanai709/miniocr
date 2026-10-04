using MiniOcr.Models;

namespace MiniOcr.Services;

public static class OcrTextDebug
{
    public static OcrTextDebugResponse From(OcrResponse ocr, string mode)
    {
        double msPerPage = ocr.PageCount > 0 ? ocr.Timings.OcrMs / ocr.PageCount : 0;
        var pages = new List<OcrTextDebugPage>(ocr.Pages.Count);
        foreach (OcrPageResult page in ocr.Pages)
        {
            pages.Add(new OcrTextDebugPage
            {
                Page = page.Page,
                Width = page.Width,
                Height = page.Height,
                RasterizeMs = page.RasterizeMs,
                OcrMs = page.OcrMs,
                Text = page.Text ?? "",
                Source = string.IsNullOrEmpty(page.Source) ? "ocr" : page.Source,
            });
        }

        return new OcrTextDebugResponse
        {
            Ok = ocr.Ok,
            Mode = mode,
            Source = ocr.DownloadMode,
            Dpi = ocr.Dpi,
            PageCount = ocr.PageCount,
            MsPerPage = Math.Round(msPerPage, 1),
            Timings = ocr.Timings,
            TextLayerMode = string.IsNullOrEmpty(ocr.TextLayerMode) ? "auto" : ocr.TextLayerMode,
            TextLayerPages = ocr.TextLayerPageCount,
            OcrPages = ocr.OcrPageCount,
            Pages = pages,
        };
    }
}
