namespace MiniOcr.Services;

/// <summary>
/// Drops high-saturation red seal ink on a BGRA page before detection.
/// Replacement is the paper color sampled from the corners, or white when that
/// sample is empty. Gray8 has no red channel; <see cref="Apply"/> leaves it unchanged.
/// </summary>
public static partial class RedSealFilter
{
    /// <summary>
    /// Rewrites BGRA pixels in place. Returns how many pixels were replaced.
    /// <paramref name="stride"/> is the row pitch in bytes.
    /// </summary>
    public static int ApplyBgra(Span<byte> pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
            return 0;
        int needed = stride * height;
        if (pixels.Length < needed)
            return 0;

        SamplePaper(pixels, width, height, stride, out byte paperB, out byte paperG, out byte paperR);
        int replaced = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
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
