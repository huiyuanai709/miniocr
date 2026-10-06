using System.Runtime.InteropServices;
using PDFtoImage;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using SkiaSharp;

// Native AOT smoke for ocr.backend=metal. Prints CPU vs Metal line diffs.
// Exit 0 when Metal stays on the GPU. Small fp16 text differences are reported, not failed.

if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
{
    Console.WriteLine($"metal smoke skipped: need osx-arm64, this process is {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
    return OperatingSystem.IsMacOS() ? 1 : 0;
}

string pdfPath = args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    ?? "samples/sample-multipage.pdf";
if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine("missing pdf: " + pdfPath);
    return 1;
}

OcrMetal.MetalDeviceInfo? probed = OcrMetal.TryProbe();
if (probed is null)
{
    Console.Error.WriteLine("Metal device probe returned null");
    return 1;
}
OcrMetal.MetalDeviceInfo device = probed.Value;
Console.WriteLine(
    $"Metal device: {device.Name} recommendedWorkingSetMB={device.RecommendedMaxWorkingSetBytes / (1024 * 1024)} bufferCapMB={device.BufferByteCap / (1024 * 1024)}");

byte[] pdf = File.ReadAllBytes(pdfPath);
using SKBitmap bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 96) { NativeGrayscale = true });
(byte[] pixels, int width, int height, int stride, ImagePixelFormat format) = CopyPixels(bitmap);
Console.WriteLine($"page {width}x{height} stride={stride} format={format}");

PaddleOcrModelBundle bundle = ChineseV6TinyModels.Default;
bundle = new PaddleOcrModelBundle(
    bundle.Name + "-nocls",
    bundle.LanguageCode,
    bundle.Detection,
    bundle.Recognition,
    bundle.Dictionary,
    classification: null!);

using PaddleOcrAll cpu = Load(bundle, OcrBackend.Cpu);
using PaddleOcrAll metal = Load(bundle, OcrBackend.Metal);
string cpuText = cpu.Run(pixels, width, height, stride, format).Text ?? "";
string metalText = metal.Run(pixels, width, height, stride, format).Text ?? "";

string[] cpuLines = Lines(cpuText);
string[] metalLines = Lines(metalText);
int compared = Math.Max(cpuLines.Length, metalLines.Length);
int differing = 0;
for (int i = 0; i < compared; i++)
{
    string left = i < cpuLines.Length ? cpuLines[i] : "";
    string right = i < metalLines.Length ? metalLines[i] : "";
    if (left != right)
    {
        differing++;
        if (differing <= 30)
            Console.WriteLine($"diff {i + 1}: cpu={left} metal={right}");
    }
}
Console.WriteLine($"cpuLines={cpuLines.Length} metalLines={metalLines.Length} differingLines={differing}");
Console.WriteLine($"metalSessionFallbacks={OcrMetal.SessionFallbackCount}");
if (OcrMetal.SessionFallbackCount != 0)
{
    Console.Error.WriteLine("Metal session fell back to CPU (shaders or device alloc failed)");
    return 1;
}
if (cpuLines.Length > 0 && metalLines.Length == 0)
{
    Console.Error.WriteLine("Metal recognized no lines while CPU did");
    return 1;
}
return 0;

static PaddleOcrAll Load(PaddleOcrModelBundle bundle, OcrBackend backend)
{
    var options = new PaddleOcrOptions
    {
        LineWorkerCount = 2,
        DetIntraOpThreads = 1,
        RecIntraOpThreads = 1,
        UseDirectionClassification = false,
        RecBatchLines = 8,
        Detector = new PaddleOcrDetectorOptions
        {
            Backend = backend,
            LimitSideLength = 960,
            MaxPooledSessions = 1,
        },
        Recognizer = new PaddleOcrRecognizerOptions
        {
            Backend = backend,
            MaxPooledSessions = 2,
        },
        Classifier = new PaddleOcrClassifierOptions
        {
            Backend = backend,
            MaxPooledSessions = 1,
        },
    };
    return PaddleOcrAll.Load(bundle, options);
}

static string[] Lines(string text) =>
    text.Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static (byte[] Pixels, int Width, int Height, int Stride, ImagePixelFormat Format) CopyPixels(SKBitmap bitmap)
{
    int width = bitmap.Width;
    int height = bitmap.Height;
    int rowBytes = bitmap.RowBytes;
    byte[] src = new byte[rowBytes * height];
    Marshal.Copy(bitmap.GetPixels(), src, 0, src.Length);
    if (bitmap.ColorType == SKColorType.Gray8)
        return (src, width, height, rowBytes, ImagePixelFormat.Gray8);

    byte[] bgr = new byte[width * height * 3];
    int channels = bitmap.BytesPerPixel;
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int s = y * rowBytes + x * channels;
            int d = (y * width + x) * 3;
            bgr[d] = src[s];
            bgr[d + 1] = channels > 1 ? src[s + 1] : src[s];
            bgr[d + 2] = channels > 2 ? src[s + 2] : src[s];
        }
    }
    return (bgr, width, height, width * 3, ImagePixelFormat.Bgr24);
}
