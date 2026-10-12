namespace MiniOcr.Services;

/// <summary>
/// Drops high-saturation red seal ink on a BGRA page before detection.
/// Replacement is the paper color sampled from the corners, or white when that
/// sample is empty. Gray8 has no red channel; <see cref="Apply"/> leaves it unchanged.
/// </summary>
/// <remarks>
/// Two modes. <c>minSealSidePx &lt;= 0</c> erases every saturated red pixel, which also
/// deletes red letterhead text, red clause numbers and red signatures. The default
/// mode finds connected red blobs on a 1/4-resolution mask and erases red only inside
/// blobs that are seal-shaped: both sides at least <c>minSealSidePx</c> and the long
/// side no more than <see cref="MaxSealAspect"/>× the short side. A red title line is
/// wide and short, so it survives and still reaches the detector.
/// </remarks>
public static partial class RedSealFilter
{
    /// <summary>Downsample factor for the blob mask. Gaps under this many pixels still connect.</summary>
    private const int Cell = 4;
    /// <summary>A blob whose long side is more than this many times its short side is text or a rule, not a seal.</summary>
    public const double MaxSealAspect = 2.5;
    /// <summary>Full-resolution margin added around a seal box so anti-aliased ring edges are included.</summary>
    private const int BoxMargin = 6;

    /// <summary>
    /// Smallest seal side in pixels for a page rendered at <paramref name="dpi"/>: about 14 mm.
    /// A 法人名章 (20 mm) and a 公章 (38–45 mm) pass; a 36 pt red title (12.7 mm) does not.
    /// </summary>
    public static int MinSealSideForDpi(int dpi) =>
        Math.Max(24, (int)Math.Round(Math.Max(36, dpi) * 0.55));

    /// <summary>
    /// Rewrites BGRA pixels in place, erasing every saturated red pixel. Returns how many were replaced.
    /// <paramref name="stride"/> is the row pitch in bytes.
    /// </summary>
    public static int ApplyBgra(Span<byte> pixels, int width, int height, int stride) =>
        ApplyBgra(pixels, width, height, stride, minSealSidePx: 0);

    /// <summary>
    /// Rewrites BGRA pixels in place. <paramref name="minSealSidePx"/> &gt; 0 erases red only inside
    /// seal-shaped blobs; 0 erases all saturated red. Returns how many pixels were replaced.
    /// </summary>
    public static int ApplyBgra(Span<byte> pixels, int width, int height, int stride, int minSealSidePx)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
            return 0;
        int needed = stride * height;
        if (pixels.Length < needed)
            return 0;

        if (minSealSidePx <= 0)
        {
            SamplePaper(pixels, width, height, stride, out byte paperB, out byte paperG, out byte paperR);
            return EraseRed(pixels, width, height, stride, 0, 0, width, height, paperB, paperG, paperR);
        }

        List<(int X0, int Y0, int X1, int Y1)> boxes = FindSealBoxes(pixels, width, height, stride, minSealSidePx);
        if (boxes.Count == 0)
            return 0;

        SamplePaper(pixels, width, height, stride, out byte b, out byte g, out byte r);
        int replaced = 0;
        foreach ((int x0, int y0, int x1, int y1) in boxes)
            replaced += EraseRed(pixels, width, height, stride, x0, y0, x1, y1, b, g, r);
        return replaced;
    }

    /// <summary>
    /// Bounding boxes (exclusive max) of red blobs that look like seals. Exposed for tests.
    /// </summary>
    public static List<(int X0, int Y0, int X1, int Y1)> FindSealBoxes(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        int minSealSidePx)
    {
        int cw = (width + Cell - 1) / Cell;
        int ch = (height + Cell - 1) / Cell;
        byte[] mask = new byte[cw * ch];
        bool any = false;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            int maskRow = (y / Cell) * cw;
            for (int x = 0; x < width; x++)
            {
                int i = row + (x * 4);
                if (pixels[i + 3] < 250 || !IsSealRed(pixels[i + 2], pixels[i + 1], pixels[i]))
                    continue;
                mask[maskRow + (x / Cell)] = 1;
                any = true;
            }
        }

        List<(int, int, int, int)> boxes = [];
        if (!any)
            return boxes;

        int[] stack = new int[cw * ch];
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] != 1)
                continue;
            int sp = 0;
            stack[sp++] = start;
            mask[start] = 2;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            while (sp > 0)
            {
                int cell = stack[--sp];
                int cx = cell % cw;
                int cy = cell / cw;
                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if ((uint)ny >= (uint)ch)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx;
                        if ((uint)nx >= (uint)cw)
                            continue;
                        int n = (ny * cw) + nx;
                        if (mask[n] != 1)
                            continue;
                        mask[n] = 2;
                        stack[sp++] = n;
                    }
                }
            }

            int bw = (maxX - minX + 1) * Cell;
            int bh = (maxY - minY + 1) * Cell;
            if (!IsSealShaped(bw, bh, minSealSidePx))
                continue;
            int x0 = Math.Max(0, (minX * Cell) - BoxMargin);
            int y0 = Math.Max(0, (minY * Cell) - BoxMargin);
            int x1 = Math.Min(width, ((maxX + 1) * Cell) + BoxMargin);
            int y1 = Math.Min(height, ((maxY + 1) * Cell) + BoxMargin);
            boxes.Add((x0, y0, x1, y1));
        }

        return boxes;
    }

    internal static bool IsSealShaped(int boxWidth, int boxHeight, int minSealSidePx)
    {
        int shortSide = Math.Min(boxWidth, boxHeight);
        int longSide = Math.Max(boxWidth, boxHeight);
        if (shortSide < minSealSidePx)
            return false;
        return longSide <= shortSide * MaxSealAspect;
    }

    private static int EraseRed(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        int x0,
        int y0,
        int x1,
        int y1,
        byte paperB,
        byte paperG,
        byte paperR)
    {
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(width, x1);
        y1 = Math.Min(height, y1);
        int replaced = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + (x * 4);
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 250 || !IsSealRed(r, g, b))
                    continue;
                pixels[i] = paperB;
                pixels[i + 1] = paperG;
                pixels[i + 2] = paperR;
                replaced++;
            }
        }

        return replaced;
    }

    /// <summary>
    /// Bright, saturated red. Dark ink under a seal stays (value too low).
    /// Hue is within about 20 degrees of pure red.
    /// </summary>
    internal static bool IsSealRed(byte r, byte g, byte b)
    {
        if (r < 100)
            return false;
        int max = r > g ? r : g;
        if (b > max)
            max = b;
        if (r != max)
            return false;
        int min = r < g ? r : g;
        if (b < min)
            min = b;
        int delta = max - min;
        // Saturation under 0.40 is paper, gray, or a weak tint.
        if (delta * 5 < max * 2)
            return false;
        // |g-b| / delta <= 1/3 → hue within 20 degrees of 0.
        int spread = g > b ? g - b : b - g;
        return spread * 3 <= delta;
    }

    private static void SamplePaper(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        out byte b,
        out byte g,
        out byte r)
    {
        int n = Math.Min(16, Math.Min(width, height));
        int bSum = 0, gSum = 0, rSum = 0, count = 0;
        AddCorner(pixels, stride, 0, 0, n, ref bSum, ref gSum, ref rSum, ref count);
        AddCorner(pixels, stride, width - n, 0, n, ref bSum, ref gSum, ref rSum, ref count);
        AddCorner(pixels, stride, 0, height - n, n, ref bSum, ref gSum, ref rSum, ref count);
        AddCorner(pixels, stride, width - n, height - n, n, ref bSum, ref gSum, ref rSum, ref count);
        if (count < 8)
        {
            b = 255;
            g = 255;
            r = 255;
            return;
        }

        b = (byte)(bSum / count);
        g = (byte)(gSum / count);
        r = (byte)(rSum / count);
    }

    private static void AddCorner(
        ReadOnlySpan<byte> pixels,
        int stride,
        int originX,
        int originY,
        int n,
        ref int bSum,
        ref int gSum,
        ref int rSum,
        ref int count)
    {
        for (int y = 0; y < n; y++)
        {
            int row = (originY + y) * stride;
            for (int x = 0; x < n; x++)
            {
                int i = row + ((originX + x) * 4);
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                int max = r > g ? r : g;
                if (b > max)
                    max = b;
                // Skip seal ink and dark text so the sample stays the page background.
                if (max < 160 || IsSealRed(r, g, b))
                    continue;
                bSum += b;
                gSum += g;
                rSum += r;
                count++;
            }
        }
    }
}
