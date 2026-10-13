namespace MiniOcr.Services;

/// <summary>
/// Detects red seal blobs and turns each one into an image SimdPaddleOCR can read.
/// Ring seals (公章) are cropped and polar-unwrapped so the circular legend becomes
/// a straight line. Square name seals are cropped as-is. The source page is only read.
/// </summary>
public static class SealRingOcr
{
    /// <summary>Inner radius of the legend band, as a fraction of the seal radius. The star stays inside this.</summary>
    public const double RingInner = 0.58;

    /// <summary>Outer radius of the legend band. A little past the ink so antialiased heads are included.</summary>
    public const double RingOuter = 1.06;

    private const int Pad = 8;

    /// <summary>One OCR-ready BGRA image. <see cref="Stride"/> is <c>Width * 4</c>.</summary>
    public readonly record struct SealImage(byte[] Bgra, int Width, int Height)
    {
        public int Stride => Width * 4;

        public bool IsEmpty =>
            Width <= 0 || Height <= 0 || Bgra is null || Bgra.Length < Height * Math.Max(0, Stride);
    }

    /// <summary>
    /// Up to four readings of one seal: clockwise heads-out, reversed reading order,
    /// and the two vertical flips for legends whose heads point inward.
    /// A square stamp fills <see cref="Primary"/> only.
    /// </summary>
    public readonly record struct SealCapture(
        SealImage Primary,
        SealImage Reversed,
        SealImage HeadsIn,
        SealImage HeadsInReversed);

    /// <summary>
    /// Seal-shaped red blobs on a BGRA page. Does not write <paramref name="pixels"/>.
    /// <paramref name="minSealSidePx"/> is <see cref="RedSealFilter.MinSealSideForDpi"/>.
    /// </summary>
    public static List<SealCapture> Capture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        int minSealSidePx)
    {
        List<SealCapture> found = [];
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < stride * height)
            return found;

        foreach ((int x0, int y0, int x1, int y1) in RedSealFilter.FindSealBoxes(pixels, width, height, stride, minSealSidePx))
        {
            if (!TryMeasure(pixels, width, height, stride, x0, y0, x1, y1, out Ellipse ellipse))
                continue;
            found.Add(IsRing(pixels, width, height, stride, ellipse)
                ? Unwrap(pixels, width, height, stride, ellipse)
                : CropSolid(pixels, width, height, stride, ellipse));
        }

        return found;
    }

    /// <summary>
    /// Page coordinate of one pixel in an unwrapped ring, before the white padding is added.
    /// Column 0 is 6 o'clock. Clockwise matches the usual 公章 reading direction.
    /// Row 0 is the outer edge, so character heads that point outward stay upright.
    /// </summary>
    public static void RingSample(
        int column,
        int row,
        int columns,
        int rows,
        double cx,
        double cy,
        double rx,
        double ry,
        bool clockwise,
        out double x,
        out double y)
    {
        double span = RingOuter - RingInner;
        double t = rows <= 1 ? RingOuter : RingOuter - (span * row / (rows - 1));
        double turns = columns <= 0 ? 0 : (double)column / columns;
        double theta = clockwise
            ? (Math.PI / 2) + (2 * Math.PI * turns)
            : (Math.PI / 2) - (2 * Math.PI * turns);
        x = cx + (rx * t * Math.Cos(theta));
        y = cy + (ry * t * Math.Sin(theta));
    }

    /// <summary>Four or more CJK characters is enough of a company name to skip the other orientations.</summary>
    public static bool Enough(string? text) => CountCjk(text) >= 4;

    /// <summary>Picks the reading with the most CJK characters, then the longer string.</summary>
    public static string Prefer(params string?[] options)
    {
        string best = "";
        int bestCjk = -1;
        int bestLen = -1;
        foreach (string? option in options)
        {
            if (string.IsNullOrWhiteSpace(option))
                continue;
            string text = option.Replace("\r", "", StringComparison.Ordinal).Trim();
            if (text.Length == 0)
                continue;
            int cjk = CountCjk(text);
            if (cjk > bestCjk || (cjk == bestCjk && text.Length > bestLen))
            {
                best = text;
                bestCjk = cjk;
                bestLen = text.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Appends seal lines that are not already in the page text. NER and the contest
    /// mapper both read <c>page.Text</c>, so this is the merge into the existing pipeline.
    /// </summary>
    public static string MergePageText(string? pageText, string? sealText)
    {
        string page = (pageText ?? "").Replace("\r", "", StringComparison.Ordinal).Trim();
        string seal = (sealText ?? "").Replace("\r", "", StringComparison.Ordinal).Trim();
        if (seal.Length == 0)
            return page;

        List<string> lines = [];
        foreach (string raw in seal.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (page.Contains(line, StringComparison.Ordinal))
                continue;
            bool seen = false;
            foreach (string kept in lines)
            {
                if (kept.Contains(line, StringComparison.Ordinal))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
                lines.Add(line);
        }

        if (lines.Count == 0)
            return page;
        string extra = string.Join('\n', lines);
        return page.Length == 0 ? extra : page + "\n" + extra;
    }

    private readonly record struct Ellipse(double Cx, double Cy, double Rx, double Ry, int X0, int Y0, int X1, int Y1);

    private static bool TryMeasure(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        int x0,
        int y0,
        int x1,
        int y1,
        out Ellipse ellipse)
    {
        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        x1 = Math.Clamp(x1, 0, width);
        y1 = Math.Clamp(y1, 0, height);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + (x * 4);
                if (pixels[i + 3] < 250 || !RedSealFilter.IsSealRed(pixels[i + 2], pixels[i + 1], pixels[i]))
                    continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                count++;
            }
        }

        if (count < 24 || maxX < minX || maxY < minY)
        {
            ellipse = default;
            return false;
        }

        double rx = Math.Max(1, (maxX - minX + 1) / 2.0);
        double ry = Math.Max(1, (maxY - minY + 1) / 2.0);
        ellipse = new Ellipse(minX + rx - 0.5, minY + ry - 0.5, rx, ry, minX, minY, maxX + 1, maxY + 1);
        return true;
    }

    /// <summary>
    /// A 公章 is red on the rim and lighter in the gap inside that rim (the star sits further in).
    /// A filled name seal is red in both bands, so it is cropped instead of unwrapped.
    /// </summary>
    private static bool IsRing(ReadOnlySpan<byte> pixels, int width, int height, int stride, Ellipse ellipse)
    {
        int textRed = 0, textN = 0, gapRed = 0, gapN = 0;
        int x0 = Math.Clamp((int)Math.Floor(ellipse.Cx - ellipse.Rx), 0, width);
        int y0 = Math.Clamp((int)Math.Floor(ellipse.Cy - ellipse.Ry), 0, height);
        int x1 = Math.Clamp((int)Math.Ceiling(ellipse.Cx + ellipse.Rx), 0, width);
        int y1 = Math.Clamp((int)Math.Ceiling(ellipse.Cy + ellipse.Ry), 0, height);
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            double ny = (y - ellipse.Cy) / ellipse.Ry;
            for (int x = x0; x < x1; x++)
            {
                double nx = (x - ellipse.Cx) / ellipse.Rx;
                double t = Math.Sqrt((nx * nx) + (ny * ny));
                if (t > 1.02)
                    continue;
                int i = row + (x * 4);
                bool red = pixels[i + 3] >= 250 && RedSealFilter.IsSealRed(pixels[i + 2], pixels[i + 1], pixels[i]);
                if (t >= 0.70)
                {
                    textN++;
                    if (red) textRed++;
                }
                else if (t is >= 0.42 and < 0.64)
                {
                    gapN++;
                    if (red) gapRed++;
                }
            }
        }

        if (textN < 20 || gapN < 10 || textRed < 20)
            return false;
        double textDensity = (double)textRed / textN;
        double gapDensity = (double)gapRed / gapN;
        return textDensity >= 0.10 && textDensity > gapDensity + 0.05;
    }

    private static SealCapture Unwrap(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        Ellipse ellipse)
    {
        int columns = Math.Clamp((int)Math.Round(2 * Math.PI * ((ellipse.Rx + ellipse.Ry) / 2.0)), 32, 4096);
        int rows = Math.Clamp((int)Math.Round((RingOuter - RingInner) * Math.Min(ellipse.Rx, ellipse.Ry)), 12, 512);
        // A 14 mm seal at 96 DPI is only ~12 px of legend. Stretch it so detection sees a normal line.
        if (rows < 48)
        {
            double scale = 48.0 / rows;
            rows = 48;
            columns = Math.Clamp((int)Math.Round(columns * scale), 32, 4096);
        }

        SealImage primary = SampleRing(pixels, width, height, stride, ellipse, columns, rows);
        SealImage reversed = FlipHorizontal(primary);
        SealImage headsIn = FlipVertical(primary);
        SealImage headsInReversed = FlipHorizontal(headsIn);
        return new SealCapture(primary, reversed, headsIn, headsInReversed);
    }

    private static SealImage SampleRing(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        Ellipse ellipse,
        int columns,
        int rows)
    {
        int outW = columns + (Pad * 2);
        int outH = rows + (Pad * 2);
        byte[] buffer = new byte[outW * outH * 4];
        FillWhite(buffer);
        for (int row = 0; row < rows; row++)
        {
            int dstRow = (row + Pad) * outW * 4;
            for (int column = 0; column < columns; column++)
            {
                RingSample(column, row, columns, rows, ellipse.Cx, ellipse.Cy, ellipse.Rx, ellipse.Ry, clockwise: true, out double x, out double y);
                Sample(pixels, width, height, stride, x, y, out byte b, out byte g, out byte r);
                int dst = dstRow + ((column + Pad) * 4);
                WriteInk(buffer, dst, r, g, b);
            }
        }

        return new SealImage(buffer, outW, outH);
    }

    private static SealCapture CropSolid(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        Ellipse ellipse)
    {
        int cropW = Math.Max(1, ellipse.X1 - ellipse.X0);
        int cropH = Math.Max(1, ellipse.Y1 - ellipse.Y0);
        int outW = cropW + (Pad * 2);
        int outH = cropH + (Pad * 2);
        byte[] buffer = new byte[outW * outH * 4];
        FillWhite(buffer);
        for (int y = 0; y < cropH; y++)
        {
            int srcY = ellipse.Y0 + y;
            if ((uint)srcY >= (uint)height)
                continue;
            int srcRow = srcY * stride;
            int dstRow = (y + Pad) * outW * 4;
            for (int x = 0; x < cropW; x++)
            {
                int srcX = ellipse.X0 + x;
                if ((uint)srcX >= (uint)width)
                    continue;
                int src = srcRow + (srcX * 4);
                int dst = dstRow + ((x + Pad) * 4);
                WriteInk(buffer, dst, pixels[src + 2], pixels[src + 1], pixels[src]);
            }
        }

        return new SealCapture(new SealImage(buffer, outW, outH), default, default, default);
    }

    private static void Sample(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        double x,
        double y,
        out byte b,
        out byte g,
        out byte r)
    {
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            b = g = r = 255;
            return;
        }

        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        double tx = x - x0;
        double ty = y - y0;
        int i00 = (y0 * stride) + (x0 * 4);
        int i10 = (y0 * stride) + (x1 * 4);
        int i01 = (y1 * stride) + (x0 * 4);
        int i11 = (y1 * stride) + (x1 * 4);
        b = Blend(pixels[i00], pixels[i10], pixels[i01], pixels[i11], tx, ty);
        g = Blend(pixels[i00 + 1], pixels[i10 + 1], pixels[i01 + 1], pixels[i11 + 1], tx, ty);
        r = Blend(pixels[i00 + 2], pixels[i10 + 2], pixels[i01 + 2], pixels[i11 + 2], tx, ty);
    }

    private static byte Blend(byte c00, byte c10, byte c01, byte c11, double tx, double ty)
    {
        double top = c00 + ((c10 - c00) * tx);
        double bottom = c01 + ((c11 - c01) * tx);
        return (byte)Math.Clamp((int)Math.Round(top + ((bottom - top) * ty)), 0, 255);
    }

    /// <summary>Seal red becomes black text on white, which is what the Chinese recognizer expects.</summary>
    private static void WriteInk(byte[] buffer, int dst, byte r, byte g, byte b)
    {
        bool ink = RedSealFilter.IsSealRed(r, g, b) || (r >= 80 && r > g + 25 && r > b + 25);
        byte v = ink ? (byte)0 : (byte)255;
        buffer[dst] = v;
        buffer[dst + 1] = v;
        buffer[dst + 2] = v;
        buffer[dst + 3] = 255;
    }

    private static SealImage FlipHorizontal(SealImage source)
    {
        if (source.IsEmpty)
            return default;
        int w = source.Width;
        int h = source.Height;
        byte[] buffer = new byte[source.Bgra.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int src = row + (x * 4);
                int dst = row + ((w - 1 - x) * 4);
                buffer[dst] = source.Bgra[src];
                buffer[dst + 1] = source.Bgra[src + 1];
                buffer[dst + 2] = source.Bgra[src + 2];
                buffer[dst + 3] = source.Bgra[src + 3];
            }
        }

        return new SealImage(buffer, w, h);
    }

    private static SealImage FlipVertical(SealImage source)
    {
        if (source.IsEmpty)
            return default;
        int w = source.Width;
        int h = source.Height;
        int stride = w * 4;
        byte[] buffer = new byte[source.Bgra.Length];
        for (int y = 0; y < h; y++)
            source.Bgra.AsSpan(y * stride, stride).CopyTo(buffer.AsSpan((h - 1 - y) * stride, stride));
        return new SealImage(buffer, w, h);
    }

    private static void FillWhite(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 255;
            buffer[i + 1] = 255;
            buffer[i + 2] = 255;
            buffer[i + 3] = 255;
        }
    }

    private static int CountCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        int n = 0;
        foreach (char c in text)
        {
            if (c is >= '\u4E00' and <= '\u9FFF')
                n++;
        }

        return n;
    }
}
