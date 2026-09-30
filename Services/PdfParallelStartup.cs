using System.Text;
using PDFtoImage;
using PDFtoImage.Parallel;
using SkiaSharp;

namespace MiniOcr.Services;

/// <summary>
/// Starts one PDFtoImage.Parallel worker before the web host serves traffic.
/// Spawn failures switch this process to in-process rendering and leave a single log line.
/// </summary>
public static class PdfParallelStartup
{
    public static async Task<(OcrRuntimeConfig Config, ParallelPdfProcessor? Processor)> ProbeAsync(
        OcrRuntimeConfig config,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!config.IsParallelRender)
            return (config, null);

        try
        {
            ParallelPdfProcessor processor = await StartAndRenderProbeAsync(config, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Parallel PDF render workers started (processes={Processes})",
                config.RenderProcessCount);
            return (config, processor);
        }
        catch (Exception ex)
        {
            OcrRuntimeConfig fallback = ParallelStartupFallback.Apply(config, ex, message =>
            {
                Console.Error.WriteLine(message);
                logger.LogWarning(ex, "{Message}", message);
            });
            return (fallback, null);
        }
    }

    private static async Task<ParallelPdfProcessor> StartAndRenderProbeAsync(
        OcrRuntimeConfig config,
        CancellationToken ct)
    {
        var processor = new ParallelPdfProcessor(new ProcessorOptions
        {
            WorkerCount = Math.Clamp(config.RenderProcessCount, 1, 8),
            TransferMode = ProcessorTransferMode.MemoryMappedFile,
            ReuseFileStream = true,
        });
        try
        {
            await using MemoryStream pdf = new(ProbePdf, writable: false);
            RenderOptions options = new(
                Dpi: 36,
                WithAnnotations: false,
                WithFormFill: false,
                AntiAliasing: PdfAntiAliasing.None,
                Grayscale: true);
            await foreach (SKBitmap bitmap in processor.ToImagesAsync(
                pdf, leaveOpen: true, options: options, cancellationToken: ct).ConfigureAwait(false))
            {
                bitmap.Dispose();
            }

            return processor;
        }
        catch (Exception renderEx)
        {
            try
            {
                processor.Dispose();
            }
            catch (Exception disposeEx)
            {
                throw new AggregateException(renderEx, disposeEx);
            }

            throw;
        }
    }

    /// <summary>One empty page. Offsets are computed so the xref stays valid.</summary>
    private static byte[] ProbePdf { get; } = CreateProbePdf();

    private static byte[] CreateProbePdf()
    {
        Encoding encoding = Encoding.ASCII;
        using var ms = new MemoryStream();
        void Write(string text)
        {
            byte[] bytes = encoding.GetBytes(text);
            ms.Write(bytes);
        }

        Write("%PDF-1.4\n");
        long[] offsets = new long[4];
        void Obj(int id, string body)
        {
            offsets[id] = ms.Position;
            Write($"{id} 0 obj\n{body}\nendobj\n");
        }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>");
        long xref = ms.Position;
        Write("xref\n0 4\n");
        Write("0000000000 65535 f \n");
        for (int i = 1; i <= 3; i++)
            Write($"{offsets[i]:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
