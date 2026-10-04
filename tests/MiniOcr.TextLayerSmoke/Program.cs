using System.Diagnostics;
using System.Text;
using MiniOcr.Models;
using MiniOcr.Services;
using PDFtoImage;
using SkiaSharp;

int failed = 0;

void AssertTrue(bool cond, string msg)
{
    if (cond)
    {
        Console.WriteLine("  PASS  " + msg);
        return;
    }

    Console.WriteLine("  FAIL  " + msg);
    failed++;
}

PdfTextLayer.Thresholds thresholds = PdfTextLayer.Defaults;

Console.WriteLine("=== classifier ===");
PdfPageAnalysis textPage = new(
    new PdfPageText("The quick brown fox jumps over the lazy dog. The quick brown fox jumps again.", 80, 0),
    new PdfPageContentStats(1, 0, 0.0));
PdfTextLayer.Decision textDecision = PdfTextLayer.Classify("auto", textPage, thresholds);
AssertTrue(textDecision.Use && textDecision.Reason == "text", "plain text page is usable (" + textDecision.Reason + ")");

PdfPageAnalysis scan = new(
    new PdfPageText("", 0, 0),
    new PdfPageContentStats(0, 0, 0.98));
PdfTextLayer.Decision scanDecision = PdfTextLayer.Classify("auto", scan, thresholds);
AssertTrue(!scanDecision.Use && scanDecision.Reason == "no-text", "empty scan is OCR");

string caption = "Figure 1";
PdfPageAnalysis captionPage = new(
    new PdfPageText(caption, caption.Length, 0),
    new PdfPageContentStats(1, 0, 0.92));
PdfTextLayer.Decision captionDecision = PdfTextLayer.Classify("auto", captionPage, thresholds);
AssertTrue(!captionDecision.Use && captionDecision.Reason == "image-little-text", "caption over a scan is OCR");

string hidden = new string('A', 240);
PdfPageAnalysis hiddenGood = new(
    new PdfPageText(hidden, hidden.Length, 0),
    new PdfPageContentStats(4, 4, 0.95));
PdfTextLayer.Decision hiddenDecision = PdfTextLayer.Classify("auto", hiddenGood, thresholds);
AssertTrue(hiddenDecision.Use && hiddenDecision.Reason == "invisible-layer", "good invisible OCR layer is usable");

PdfPageAnalysis hiddenBad = new(
    new PdfPageText(new string('\uFFFD', 40) + new string('B', 200), 240, 40),
    new PdfPageContentStats(4, 4, 0.95));
PdfTextLayer.Decision hiddenBadDecision = PdfTextLayer.Classify("auto", hiddenBad, thresholds);
AssertTrue(!hiddenBadDecision.Use && hiddenBadDecision.Reason == "unknown-chars", "garbled invisible layer is OCR");

PdfTextLayer.Decision forced = PdfTextLayer.Classify("force", captionPage, thresholds);
AssertTrue(forced.Use && forced.Reason == "force", "force keeps a short caption");
PdfTextLayer.Decision forcedEmpty = PdfTextLayer.Classify("force", scan, thresholds);
AssertTrue(!forcedEmpty.Use, "force still OCRs a page with no text");
PdfTextLayer.Decision off = PdfTextLayer.Classify("off", textPage, thresholds);
AssertTrue(!off.Use && off.Reason == "off", "off never uses the text layer");
AssertTrue(PdfTextLayer.CanonicalMode("nope") == "auto", "unknown mode is auto");

string normalized = PdfTextLayer.Normalize("Alpha\r\nBeta\rGamma\fDelta  ");
AssertTrue(normalized == "Alpha\nBeta\nGamma\nDelta", "line breaks become LF (" + normalized.Replace("\n", "\\n") + ")");

Console.WriteLine("=== generated PDFs ===");
string fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
Directory.CreateDirectory(fixtureDir);
byte[] textPdf = PdfFixture.TextPdf(
    "Alpha line one is long enough to keep.\nBeta line two continues the paragraph.",
    "北京华为技术有限公司法定代表人张伟。北京华为技术有限公司法定代表人张伟。北京华为技术有限公司法定代表人张伟。北京华为技术有限公司法定代表人张伟。");
byte[] scanPdf = PdfFixture.ScanPdf(includeText: false, invisible: false, literal: "");
byte[] mixedPdf = PdfFixture.MixedPdf();
File.WriteAllBytes(Path.Combine(fixtureDir, "text.pdf"), textPdf);
File.WriteAllBytes(Path.Combine(fixtureDir, "scan.pdf"), scanPdf);
File.WriteAllBytes(Path.Combine(fixtureDir, "mixed.pdf"), mixedPdf);
File.WriteAllBytes(Path.Combine(fixtureDir, "text-40.pdf"), PdfFixture.RepeatedTextPdf(40, "Paragraph on this page. The quick brown fox jumps over the lazy dog. "));
File.WriteAllBytes(Path.Combine(fixtureDir, "mixed-30.pdf"), PdfFixture.RepeatedMixedPdf(textPages: 20, scanPages: 10));

using (PdfSession textSession = PdfSession.Open(new MemoryStream(textPdf), leaveOpen: true))
{
    AssertTrue(textSession.PageCount == 2, "text pdf has 2 pages");
    PdfPageAnalysis first = textSession.AnalyzePage(0);
    PdfTextLayer.Decision firstDecision = PdfTextLayer.Classify("auto", first, thresholds);
    string firstText = PdfTextLayer.Normalize(first.Text.Text);
    AssertTrue(firstDecision.Use, "text pdf page 1 is text layer (" + firstDecision.Reason + ")");
    AssertTrue(firstText.Contains("Alpha line one", StringComparison.Ordinal), "page 1 keeps the first line");
    AssertTrue(firstText.Contains('\n') && firstText.Contains("Beta line two", StringComparison.Ordinal), "page 1 keeps a line break");
    PdfPageAnalysis second = textSession.AnalyzePage(1);
    string secondText = PdfTextLayer.Normalize(second.Text.Text);
    AssertTrue(secondText.Contains("北京华为技术有限公司", StringComparison.Ordinal), "page 2 keeps the company name");
    AssertTrue(secondText.Contains("张伟", StringComparison.Ordinal), "page 2 keeps the person name");
    AssertTrue(PdfTextLayer.Classify("auto", second, thresholds).Use, "text pdf page 2 is text layer");
}

using (PdfSession scanSession = PdfSession.Open(new MemoryStream(scanPdf), leaveOpen: true))
{
    PdfPageAnalysis only = scanSession.AnalyzePage(0);
    PdfTextLayer.Decision onlyDecision = PdfTextLayer.Classify("auto", only, thresholds);
    AssertTrue(!onlyDecision.Use && onlyDecision.Reason == "no-text", "scan pdf is OCR (" + onlyDecision.Reason + ")");
    AssertTrue(only.Content.ImageAreaCoverage > 0.8, "scan covers the page");
}

using (PdfSession mixed = PdfSession.Open(new MemoryStream(mixedPdf), leaveOpen: true))
{
    AssertTrue(mixed.PageCount == 4, "mixed pdf has 4 pages");
    PdfTextLayer.Decision[] decisions = new PdfTextLayer.Decision[4];
    string[] reasons = ["text", "no-text", "invisible-layer", "image-little-text"];
    for (int i = 0; i < 4; i++)
    {
        decisions[i] = PdfTextLayer.Classify("auto", mixed.AnalyzePage(i), thresholds);
        AssertTrue(decisions[i].Reason == reasons[i], $"mixed page {i + 1} reason {decisions[i].Reason} expected {reasons[i]}");
    }

    AssertTrue(decisions[0].Use && !decisions[1].Use && decisions[2].Use && !decisions[3].Use, "mixed pdf uses text on pages 1 and 3 only");
    string hiddenText = PdfTextLayer.Normalize(mixed.GetText(2).Text);
    AssertTrue(hiddenText.Contains("Hidden layer", StringComparison.Ordinal), "invisible layer text is readable");
}

Console.WriteLine("=== NER text shape ===");
string pageText = PdfTextLayer.Normalize("甲方：北京华为\r\n技术有限公司\r\n法定代表人：张伟");
OcrPageResult page = new()
{
    Page = 1,
    Text = pageText,
    Source = PdfTextLayer.SourceTextLayer,
};
string grouped = LlmPageGrouper.FormatPage(page);
AssertTrue(grouped.Contains("北京华为\n技术有限公司", StringComparison.Ordinal), "NER group keeps the line break");
AssertTrue(EntityText.Repair(pageText).Contains("北京华为技术有限公司", StringComparison.Ordinal), "post-processor joins a name split by a line break");
List<LlmPageGrouper.PageBatch> groups = LlmPageGrouper.BuildGroups([page], pagesPerRequest: 10, maxChars: 1000);
AssertTrue(groups.Count == 1 && groups[0].PageNumbers is [1], "one non-empty text-layer page forms a NER group");

Console.WriteLine("=== benchmark ===");
byte[] manyText = PdfFixture.RepeatedTextPdf(40, "Paragraph on this page. The quick brown fox jumps over the lazy dog. ");
byte[] manyMixed = PdfFixture.RepeatedMixedPdf(textPages: 20, scanPages: 10);
Benchmark("text-40 analyze", () => AnalyzeAll(manyText));
Benchmark("text-40 render", () => RenderAll(manyText));
Benchmark("mixed-30 analyze", () => AnalyzeAll(manyMixed));
Benchmark("mixed-30 render-all", () => RenderAll(manyMixed));
Benchmark("mixed-30 analyze+render-scans", () =>
{
    using PdfSession session = PdfSession.Open(new MemoryStream(manyMixed), leaveOpen: true);
    List<int> scans = [];
    for (int i = 0; i < session.PageCount; i++)
    {
        if (!PdfTextLayer.Classify("auto", session.AnalyzePage(i), thresholds).Use)
            scans.Add(i);
    }

    foreach (SKBitmap bitmap in session.RenderPages(scans, new RenderOptions(Dpi: 72, AntiAliasing: PdfAntiAliasing.None, Grayscale: true)))
        bitmap.Dispose();
});

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    return 1;
}

Console.WriteLine("All text-layer checks passed.");
return 0;

static void AnalyzeAll(byte[] pdf)
{
    using PdfSession session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);
    for (int i = 0; i < session.PageCount; i++)
        _ = session.AnalyzePage(i);
}

static void RenderAll(byte[] pdf)
{
    using PdfSession session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);
    int[] pages = new int[session.PageCount];
    for (int i = 0; i < pages.Length; i++)
        pages[i] = i;
    foreach (SKBitmap bitmap in session.RenderPages(pages, new RenderOptions(Dpi: 72, AntiAliasing: PdfAntiAliasing.None, Grayscale: true)))
        bitmap.Dispose();
}

static void Benchmark(string name, Action action)
{
    action();
    Stopwatch sw = Stopwatch.StartNew();
    action();
    sw.Stop();
    Console.WriteLine($"  BENCH {name} {sw.Elapsed.TotalMilliseconds:F1} ms");
}

static class PdfFixture
{
    public static byte[] TextPdf(params string[] pages)
    {
        var built = new List<PageBody>();
        foreach (string page in pages)
            built.Add(TextBody(page));
        return Assemble(built);
    }

    public static byte[] ScanPdf(bool includeText, bool invisible, string literal) =>
        Assemble([ScanBody(includeText, invisible, literal)]);

    public static byte[] MixedPdf()
    {
        string hidden = "Hidden layer " + new string('A', 220);
        return Assemble([
            TextBody("Alpha line one is long enough to keep.\nBeta line two continues the paragraph."),
            ScanBody(includeText: false, invisible: false, literal: ""),
            ScanBody(includeText: true, invisible: true, literal: hidden),
            ScanBody(includeText: true, invisible: false, literal: "Figure 1"),
        ]);
    }

    public static byte[] RepeatedTextPdf(int pages, string line)
    {
        var built = new List<PageBody>(pages);
        for (int i = 0; i < pages; i++)
            built.Add(TextBody($"Page {i + 1}. {line}"));
        return Assemble(built);
    }

    public static byte[] RepeatedMixedPdf(int textPages, int scanPages)
    {
        var built = new List<PageBody>(textPages + scanPages);
        for (int i = 0; i < textPages; i++)
            built.Add(TextBody($"Text page {i + 1}. The quick brown fox jumps over the lazy dog."));
        for (int i = 0; i < scanPages; i++)
            built.Add(ScanBody(false, false, ""));
        return Assemble(built);
    }

    private readonly record struct PageBody(string Content, string Resources, byte[][] Streams);

    private static PageBody TextBody(string text)
    {
        string content = BuildTextContent(text, invisible: false);
        bool chinese = text.Any(c => c > 127);
        if (!chinese)
        {
            return new PageBody(
                content,
                "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>",
                []);
        }

        return new PageBody(
            content,
            "/Font << /F1 0 0 R >>",
            []);
    }

    private static string BuildTextContent(string text, bool invisible)
    {
        var sb = new StringBuilder();
        sb.Append("BT /F1 12 Tf ");
        if (invisible)
            sb.Append("3 Tr ");
        sb.Append("72 720 Td ");
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append("0 -16 Td ");
            if (lines[i].Any(c => c > 127))
                sb.Append(ChineseHex(lines[i])).Append(" Tj ");
            else
                sb.Append('(').Append(Escape(lines[i])).Append(") Tj ");
        }

        sb.Append("ET");
        return sb.ToString();
    }

    private static PageBody ScanBody(bool includeText, bool invisible, string literal)
    {
        byte[] jpeg = GrayJpeg(64, 80);
        string color = JpegComponents(jpeg) == 1 ? "/DeviceGray" : "/DeviceRGB";
        var content = new StringBuilder();
        content.Append("q\n612 0 0 792 0 0 cm\n/Im0 Do\nQ\n");
        if (includeText)
            content.Append(BuildTextContent(literal, invisible));
        string font = includeText
            ? " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>"
            : "";
        string image = "<< /Type /XObject /Subtype /Image /Width 64 /Height 80 /ColorSpace " + color +
                       " /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>";
        return new PageBody(
            content.ToString(),
            "/XObject << /Im0 0 0 R >>" + font,
            [Combine(image, jpeg)]);
    }

    private static byte[] Assemble(List<PageBody> pages)
    {
        // Object ids: 1 catalog, 2 pages, then per page: page, contents, extra streams.
        // Chinese text pages add a font object shared once.
        bool needCjk = pages.Exists(p => p.Resources.Contains("/F1 0 0 R", StringComparison.Ordinal));
        using var output = new MemoryStream();
        var offsets = new List<long> { 0 };
        Write(output, "%PDF-1.4\n");
        int next = 3;
        int[] pageIds = new int[pages.Count];
        int[] contentIds = new int[pages.Count];
        List<int>[] extraIds = new List<int>[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            pageIds[i] = next++;
            contentIds[i] = next++;
            extraIds[i] = [];
            for (int s = 0; s < pages[i].Streams.Length; s++)
                extraIds[i].Add(next++);
        }

        int fontId = needCjk ? next++ : 0;
        int cidId = needCjk ? next++ : 0;
        int cmapId = needCjk ? next++ : 0;
        int descId = needCjk ? next++ : 0;

        Start(output, offsets, 1);
        Write(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Start(output, offsets, 2);
        Write(output, "<< /Type /Pages /Count " + pages.Count + " /Kids [");
        foreach (int id in pageIds)
            Write(output, " " + id + " 0 R");
        Write(output, " ] >>\nendobj\n");

        for (int i = 0; i < pages.Count; i++)
        {
            string resources = pages[i].Resources.Replace("/F1 0 0 R", "/F1 " + fontId + " 0 R", StringComparison.Ordinal);
            resources = RewriteXObjects(resources, extraIds[i]);
            byte[] content = Encoding.ASCII.GetBytes(pages[i].Content);
            Start(output, offsets, pageIds[i]);
            Write(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << " + resources + " >> /Contents " + contentIds[i] + " 0 R >>\nendobj\n");
            Start(output, offsets, contentIds[i]);
            Write(output, "<< /Length " + content.Length + " >>\nstream\n");
            output.Write(content);
            Write(output, "\nendstream\nendobj\n");
            for (int s = 0; s < extraIds[i].Count; s++)
            {
                Start(output, offsets, extraIds[i][s]);
                output.Write(pages[i].Streams[s]);
                Write(output, "\nendstream\nendobj\n");
            }
        }

        if (needCjk)
            WriteCjkFont(output, offsets, fontId, cidId, cmapId, descId);

        Finish(output, offsets);
        return output.ToArray();
    }

    private static string RewriteXObjects(string resources, List<int> extraIds)
    {
        if (extraIds.Count == 0)
            return resources;
        return resources.Replace("/Im0 0 0 R", "/Im0 " + extraIds[0] + " 0 R", StringComparison.Ordinal);
    }

    private static void WriteCjkFont(Stream output, List<long> offsets, int fontId, int cidId, int cmapId, int descId)
    {
        const string cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n" +
            "<0000> <FFFF>\n" +
            "endcodespacerange\n" +
            "1 beginbfrange\n" +
            "<0000> <FFFF> <0000>\n" +
            "endbfrange\n" +
            "endcmap\n" +
            "CMapName currentdict /CMap defineresource pop\n" +
            "end\n" +
            "end\n";
        byte[] cmapBytes = Encoding.ASCII.GetBytes(cmap);
        Start(output, offsets, fontId);
        Write(output, "<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /Identity-H /DescendantFonts [" + cidId + " 0 R] /ToUnicode " + cmapId + " 0 R >>\nendobj\n");
        Start(output, offsets, cidId);
        Write(output, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /STSong-Light /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor " + descId + " 0 R /DW 1000 /CIDToGIDMap /Identity >>\nendobj\n");
        Start(output, offsets, cmapId);
        Write(output, "<< /Length " + cmapBytes.Length + " >>\nstream\n");
        output.Write(cmapBytes);
        Write(output, "\nendstream\nendobj\n");
        Start(output, offsets, descId);
        Write(output, "<< /Type /FontDescriptor /FontName /STSong-Light /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>\nendobj\n");
    }

    private static string ChineseHex(string text)
    {
        var sb = new StringBuilder();
        sb.Append('<');
        foreach (char c in text)
        {
            if (c > 127)
                sb.Append(((int)c).ToString("X4"));
            else if (c == ' ')
                sb.Append("0020");
        }

        sb.Append('>');
        return sb.ToString();
    }

    private static byte[] Combine(string dict, byte[] stream)
    {
        byte[] head = Encoding.ASCII.GetBytes(dict + "\nstream\n");
        byte[] tail = new byte[head.Length + stream.Length];
        head.CopyTo(tail, 0);
        stream.CopyTo(tail, head.Length);
        return tail;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    private static byte[] GrayJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        Span<byte> pixels = bitmap.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(40 + (i * 17 % 180));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 70);
        return data.ToArray();
    }

    private static int JpegComponents(byte[] jpeg)
    {
        for (int i = 0; i < jpeg.Length - 8; i++)
        {
            if (jpeg[i] != 0xFF)
                continue;
            byte marker = jpeg[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2)
                return jpeg[i + 9];
        }

        return 3;
    }

    private static void Start(Stream output, List<long> offsets, int id)
    {
        while (offsets.Count < id)
            offsets.Add(0);
        if (offsets.Count == id)
            offsets.Add(output.Position);
        else
            offsets[id] = output.Position;
        Write(output, id + " 0 obj\n");
    }

    private static void Finish(Stream output, List<long> offsets)
    {
        long xref = output.Position;
        Write(output, "xref\n0 " + offsets.Count + "\n");
        Write(output, "0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
            Write(output, offsets[i].ToString("D10") + " 00000 n \n");
        Write(output, "trailer\n<< /Size " + offsets.Count + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
    }

    private static void Write(Stream output, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        output.Write(bytes);
    }
}
