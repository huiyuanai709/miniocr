using System.Diagnostics;
using System.Globalization;
using System.Text;
using MiniOcr.Models;
using PDFtoImage;
using SkiaSharp;

namespace MiniOcr.Services;

/// <summary>
/// Sequential single-instance benchmark: one WeChatOCR process vs one Paddle engine,
/// same rasterized pages. Writes <c>wechat-vs-local.txt</c> for pasting.
/// </summary>
public static class OcrCompareRunner
{
    public static bool IsRequested(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg is "--compare" or "--benchmark" or "--wechat-compare")
                return true;
            if (arg.StartsWith("--compare=", StringComparison.Ordinal) ||
                arg.StartsWith("--benchmark=", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static async Task<int> RunAsync(string[] args, AppConfigFile appConfig, ILoggerFactory logs)
    {
        ParsedArgs parsed = Parse(args);
        ILogger logger = logs.CreateLogger("MiniOcr.Compare");
        if (string.IsNullOrWhiteSpace(parsed.PdfPath))
        {
            Console.Error.WriteLine(
                "Usage: MiniOcr --compare <pdf-path> [--pages N] [--dpi N] [--out file.txt]");
            return 2;
        }

        OcrRuntimeConfig config = OcrRuntimeConfig.FromAppConfig(appConfig).With(wechatInstances: 1, engineCount: 1);
        if (!WeChatStartup.IsWindowsX64())
        {
            Console.Error.WriteLine(
                "WeChat vs local benchmark requires Windows x64. This process cannot load mmmojo_64.dll.");
            return 2;
        }

        WeChatOcrLocation location = WeChatOcrLocator.Locate(
            WeChatLocateInput.FromConfig(config),
            FileSystemWeChatProbe.Instance);
        Console.WriteLine(location.Report);
        if (!location.Found)
        {
            Console.Error.WriteLine("Cannot benchmark: WeChat OCR plugin or install directory was not found.");
            return 1;
        }

        int dpi = Math.Clamp(parsed.Dpi ?? config.DefaultDpi, 36, 300);
        byte[] pdf = LocalPdfFile.ReadAllBytes(parsed.PdfPath);
        int pageCount;
        using (var count = new MemoryStream(pdf, writable: false))
            pageCount = Conversion.GetPageCount(count, leaveOpen: false);
        if (pageCount <= 0)
        {
            Console.Error.WriteLine("PDF has no pages.");
            return 1;
        }

        int limit = parsed.Pages is > 0 ? Math.Min(parsed.Pages.Value, pageCount) : pageCount;
        limit = Math.Min(limit, 2000);

        WeChatOcrEngine? wechat = null;
        OcrEngine? local = null;
        try
        {
            wechat = await WeChatOcrEngine.ConnectAsync(location, config, logger, CancellationToken.None, instanceCount: 1)
                .ConfigureAwait(false);
            Console.WriteLine("Loading one ChineseV6Small engine for the local side of the comparison...");
            local = await OcrEngine.CreateAsync(logs.CreateLogger<OcrEngine>(), config, CancellationToken.None)
                .ConfigureAwait(false);

            var rows = new List<PageRow>(limit);
            RenderOptions render = PdfParallelOptions.CreateRenderOptions(dpi, config.RemoveRedSeal);

            for (int i = 0; i < limit; i++)
            {
                using var stream = new MemoryStream(pdf, writable: false);
                using IEnumerator<SKBitmap> enumerator = Conversion
                    .ToImages(stream, [i], leaveOpen: true, options: render)
                    .GetEnumerator();
                if (!enumerator.MoveNext())
                    throw new InvalidOperationException($"PDFtoImage did not yield page {i + 1}.");
                using SKBitmap bitmap = enumerator.Current;
                RedSealFilter.Apply(bitmap);

                PageRow row = new() { Page = i + 1, Width = bitmap.Width, Height = bitmap.Height };
                row.WeChat = await TimeAsync(() => wechat.RecognizeBitmapAsync(bitmap, CancellationToken.None))
                    .ConfigureAwait(false);
                row.Local = await TimeAsync(() => RecognizeLocalAsync(local, bitmap, CancellationToken.None))
                    .ConfigureAwait(false);
                rows.Add(row);
                Console.WriteLine(
                    FormattableString.Invariant(
                        $"page {row.Page}  wechat_ms={row.WeChat.Ms:F1}  local_ms={row.Local.Ms:F1}  wechat_chars={row.WeChat.Text.Length}  local_chars={row.Local.Text.Length}"));
            }

            string text = FormatReport(parsed.PdfPath, dpi, location, rows);
            string outPath = Path.GetFullPath(parsed.OutPath ?? "wechat-vs-local.txt");
            File.WriteAllText(outPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine(text);
            Console.WriteLine("Wrote " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Compare failed: " + ex.Message);
            logger.LogError(ex, "WeChat vs local compare failed");
            return 1;
        }
        finally
        {
            if (wechat is not null)
                await wechat.DisposeAsync().ConfigureAwait(false);
            if (local is not null)
                await local.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Side> TimeAsync(Func<Task<string>> work)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string text = await work().ConfigureAwait(false);
            sw.Stop();
            return new Side { Ms = sw.Elapsed.TotalMilliseconds, Text = text ?? "" };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Side { Ms = sw.Elapsed.TotalMilliseconds, Text = "", Error = ex.Message };
        }
    }

    private static async Task<string> RecognizeLocalAsync(OcrEngine engine, SKBitmap bitmap, CancellationToken ct)
    {
        EnsureBgra(bitmap, out SKBitmap working, out bool owned);
        try
        {
            int width = working.Width;
            int height = working.Height;
            int stride = working.RowBytes;
            IntPtr pixels = working.GetPixels();
            int byteCount = stride * height;
            Sdcb.SimdPaddleOCR.PaddleOcrResult result = await engine.UseAsync(ocr =>
            {
                unsafe
                {
                    ReadOnlySpan<byte> span = new((void*)pixels, byteCount);
                    return ocr.Run(span, width, height, stride, Sdcb.SimdPaddleOCR.ImagePixelFormat.Bgra32);
                }
            }, ct).ConfigureAwait(false);
            return result.Text?.Replace("\r", "").Trim() ?? "";
        }
        finally
        {
            if (owned)
                working.Dispose();
        }
    }

    private static void EnsureBgra(SKBitmap source, out SKBitmap working, out bool owned)
    {
        if (source.ColorType == SKColorType.Bgra8888)
        {
            working = source;
            owned = false;
            return;
        }

        working = source.Copy(SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Failed to convert SKBitmap to BGRA8888.");
        owned = true;
    }

    private static string FormatReport(string pdf, int dpi, WeChatOcrLocation location, List<PageRow> rows)
    {
        double wechat = rows.Sum(r => r.WeChat.Ms);
        double local = rows.Sum(r => r.Local.Ms);
        int n = Math.Max(1, rows.Count);
        var sb = new StringBuilder();
        sb.AppendLine("MiniOcr wechat vs local");
        sb.AppendLine("NOTE: WeChat OCR uses an unofficial reverse-engineered mmmojo interface.");
        sb.AppendLine("It is for local comparison on this PC, not the competition server (ToS risk).");
        sb.AppendLine("Benchmark is single-threaded: 1 WeChatOCR process and 1 Paddle engine, pages in order.");
        sb.AppendLine("wechat_ms includes PNG encode + plugin. local_ms is Paddle Run. Page 1 includes warmup.");
        sb.AppendLine("pdf=" + pdf);
        sb.AppendLine(FormattableString.Invariant($"dpi={dpi} pages={rows.Count}"));
        sb.AppendLine($"wechat kind={location.KindName} plugin={location.PluginPath}");
        sb.AppendLine($"wechatDir={location.WeChatDir}");
        sb.AppendLine($"launch={location.LaunchExe}");
        sb.AppendLine(FormattableString.Invariant(
            $"TOTAL wechat_ms={wechat:F1} local_ms={local:F1} wechat_ms_per_page={wechat / n:F1} local_ms_per_page={local / n:F1}"));
        sb.AppendLine();
        sb.AppendLine("page\twechat_ms\tlocal_ms\twechat_chars\tlocal_chars\twechat_error\tlocal_error");
        foreach (PageRow row in rows)
        {
            sb.Append(row.Page.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(row.WeChat.Ms.ToString("F1", CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(row.Local.Ms.ToString("F1", CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(row.WeChat.Text.Length.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(row.Local.Text.Length.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(row.WeChat.Error ?? "").Append('\t');
            sb.AppendLine(row.Local.Error ?? "");
        }

        sb.AppendLine();
        foreach (PageRow row in rows)
        {
            sb.AppendLine($"===== page {row.Page} {row.Width}x{row.Height} =====");
            sb.AppendLine("--- wechat ---");
            sb.AppendLine(Clip(row.WeChat.Error is null ? row.WeChat.Text : row.WeChat.Error));
            sb.AppendLine("--- local ---");
            sb.AppendLine(Clip(row.Local.Error is null ? row.Local.Text : row.Local.Error));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Clip(string text)
    {
        if (text.Length <= 1500)
            return text;
        return text[..1500] + $"\n... truncated, {text.Length} chars total";
    }

    private static ParsedArgs Parse(string[] args)
    {
        var parsed = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--compare" or "--benchmark" or "--wechat-compare")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    parsed.PdfPath = args[++i];
            }
            else if (arg.StartsWith("--compare=", StringComparison.Ordinal))
            {
                parsed.PdfPath = arg["--compare=".Length..];
            }
            else if (arg.StartsWith("--benchmark=", StringComparison.Ordinal))
            {
                parsed.PdfPath = arg["--benchmark=".Length..];
            }
            else if (arg == "--pages" && i + 1 < args.Length && int.TryParse(args[i + 1], out int pages))
            {
                parsed.Pages = pages;
                i++;
            }
            else if (arg == "--dpi" && i + 1 < args.Length && int.TryParse(args[i + 1], out int dpi))
            {
                parsed.Dpi = dpi;
                i++;
            }
            else if (arg == "--out" && i + 1 < args.Length)
            {
                parsed.OutPath = args[++i];
            }
        }

        return parsed;
    }

    private sealed class ParsedArgs
    {
        public string? PdfPath { get; set; }
        public int? Pages { get; set; }
        public int? Dpi { get; set; }
        public string? OutPath { get; set; }
    }

    private sealed class PageRow
    {
        public int Page { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Side WeChat { get; set; } = new();
        public Side Local { get; set; } = new();
    }

    private sealed class Side
    {
        public double Ms { get; set; }
        public string Text { get; set; } = "";
        public string? Error { get; set; }
    }
}
