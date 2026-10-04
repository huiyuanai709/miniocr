using PDFtoImage;

namespace MiniOcr.Services;

/// <summary>
/// Decides whether a page's PDF text can replace raster + OCR.
/// <c>auto</c> keeps a real text page and a good hidden OCR layer, and still
/// rasterizes scans and image pages that only have a caption.
/// </summary>
public static class PdfTextLayer
{
    public const string ModeAuto = "auto";
    public const string ModeOff = "off";
    public const string ModeForce = "force";
    public const string SourceTextLayer = "textLayer";
    public const string SourceOcr = "ocr";

    public readonly record struct Thresholds(
        int MinChars,
        double MaxUnknownRatio,
        double ImageCoverage,
        int ImageMinChars);

    /// <summary>
    /// 40 non-whitespace characters on a normal page. An image-heavy page, including
    /// a scan with an invisible OCR layer, needs 200 and an unknown-character ratio
    /// at or under 2%.
    /// </summary>
    public static Thresholds Defaults { get; } = new(
        MinChars: 40,
        MaxUnknownRatio: 0.02,
        ImageCoverage: 0.55,
        ImageMinChars: 200);

    public readonly record struct Decision(bool Use, string Reason);

    public static string CanonicalMode(string? raw)
    {
        if (string.Equals(raw, ModeOff, StringComparison.OrdinalIgnoreCase))
            return ModeOff;
        if (string.Equals(raw, ModeForce, StringComparison.OrdinalIgnoreCase))
            return ModeForce;
        return ModeAuto;
    }

    /// <summary>PDFium line breaks become <c>\n</c>, matching Paddle page text.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\f', '\n')
            .Trim();
    }

    public static int CountNonWhitespace(string text)
    {
        int n = 0;
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
                n++;
        }

        return n;
    }

    public static Decision Classify(string? mode, PdfPageAnalysis analysis, Thresholds thresholds)
    {
        string canonical = CanonicalMode(mode);
        if (canonical == ModeOff)
            return new Decision(false, "off");

        string text = Normalize(analysis.Text.Text);
        int nonWhitespace = CountNonWhitespace(text);
        if (analysis.Content.TextObjectCount <= 0 || nonWhitespace == 0)
            return new Decision(false, "no-text");

        if (canonical == ModeForce)
            return new Decision(true, "force");

        int chars = Math.Max(analysis.Text.CharacterCount, nonWhitespace);
        double unknownRatio = chars == 0
            ? 1
            : (double)analysis.Text.UnknownCharacterCount / chars;
        if (unknownRatio > thresholds.MaxUnknownRatio)
            return new Decision(false, "unknown-chars");

        bool imageHeavy = analysis.Content.ImageAreaCoverage >= thresholds.ImageCoverage;
        bool hidden = analysis.Content.TextObjectsAreInvisible;
        int needed = imageHeavy || hidden ? Math.Max(thresholds.MinChars, thresholds.ImageMinChars) : thresholds.MinChars;
        if (nonWhitespace < needed)
            return new Decision(false, imageHeavy || hidden ? "image-little-text" : "short-text");

        return new Decision(true, hidden ? "invisible-layer" : "text");
    }
}
