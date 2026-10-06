using PDFtoImage;
using SkiaSharp;

namespace MiniOcr.Services;

/// <summary>
/// One rendered page. Parallel OCR keeps the pooled Gray8 buffer. In-process,
/// WeChat, and vision still hold an <see cref="SKBitmap"/>.
/// </summary>
internal sealed class PageImage : IDisposable
{
    private PdfPixels? _pixels;
    private SKBitmap? _bitmap;

    private PageImage(PdfPixels? pixels, SKBitmap? bitmap)
    {
        _pixels = pixels;
        _bitmap = bitmap;
        if (pixels is not null)
        {
            Width = pixels.Width;
            Height = pixels.Height;
        }
        else
        {
            Width = bitmap!.Width;
            Height = bitmap.Height;
        }
    }

    public int Width { get; }
    public int Height { get; }

    public PdfPixels? Pixels => _pixels;

    public static PageImage FromPixels(PdfPixels pixels) => new(pixels, null);

    public static PageImage FromBitmap(SKBitmap bitmap) => new(null, bitmap);

    public SKBitmap RequireBitmap() =>
        _bitmap ?? throw new InvalidOperationException("This page is Gray8 pixels, not a bitmap.");

    public void Dispose()
    {
        PdfPixels? pixels = Interlocked.Exchange(ref _pixels, null);
        SKBitmap? bitmap = Interlocked.Exchange(ref _bitmap, null);
        pixels?.Dispose();
        bitmap?.Dispose();
    }
}
