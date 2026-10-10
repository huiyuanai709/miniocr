using SkiaSharp;

namespace MiniOcr.Services;

public static partial class RedSealFilter
{
    /// <summary>
    /// In-place filter for a rendered page. Gray8 and any non-BGRA bitmap are skipped.
    /// </summary>
    public static int Apply(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Bgra8888)
            return 0;
        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = bitmap.RowBytes;
        if (width <= 0 || height <= 0 || stride < width * 4)
            return 0;
        IntPtr ptr = bitmap.GetPixels();
        int byteCount = bitmap.ByteCount;
        if (ptr == IntPtr.Zero || byteCount < stride * height)
            return 0;
        unsafe
        {
            return ApplyBgra(new Span<byte>((void*)ptr, byteCount), width, height, stride);
        }
    }
}
