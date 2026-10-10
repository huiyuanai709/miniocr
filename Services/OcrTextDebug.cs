using MiniOcr.Models;

namespace MiniOcr.Services;

public static class OcrTextDebug
{
    public static OcrTextDebugResponse From(OcrResponse ocr, string mode)
    {
        double msPerPage = ocr.PageCount > 0 ? ocr.Timings.OcrMs / ocr.PageCount : 0;
        // Same pages as POST /ocr and the challenge callback: a page is kept only
        // when BuildFileResult leaves it with at least one rule item.
        ChallengeFileResult protocol = ChallengeResultMapper.BuildFileResult("", ocr);
        var pages = new List<OcrTextDebugPage>(protocol.Pages.Count);
        foreach (ChallengePageResult hit in protocol.Pages)
        {
            if (hit.RuleList.Count == 0)
                continue;
            OcrPageResult? src = null;
            foreach (OcrPageResult page in ocr.Pages)
            {
                if (page.Page == hit.Page)
                {
                    src = page;
                    break;
                }
            }

            pages.Add(new OcrTextDebugPage
            {
                Page = hit.Page,
                Width = src?.Width ?? 0,
                Height = src?.Height ?? 0,
                RasterizeMs = src?.RasterizeMs ?? 0,
                OcrMs = src?.OcrMs ?? 0,
                Text = src?.Text ?? "",
                Source = src is null || string.IsNullOrEmpty(src.Source) ? "ocr" : src.Source,
                RuleList = hit.RuleList,
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
