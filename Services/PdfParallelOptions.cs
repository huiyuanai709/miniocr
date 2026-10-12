using PDFtoImage;
using PDFtoImage.Parallel;

namespace MiniOcr.Services;

/// <summary>
/// Options for the one <see cref="ParallelPdfProcessor"/> a node keeps for the process lifetime.
/// Page leases of the same temp PDF reuse that processor. A new processor spends about 300 ms starting workers.
/// </summary>
internal static class PdfParallelOptions
{
    public static ProcessorOptions CreateProcessor(int renderProcesses) => new()
    {
        WorkerCount = Math.Clamp(renderProcesses, 1, 8),
        // Memory-mapped bitmaps. The PDF is the one temp file written per job per node.
        // ReuseFileStream reopens that path. ShareSourceFile is the IPC form of the same
        // file share; RetainDocuments skips parsing again while path, length, mtime, and password match.
        TransferMode = ProcessorTransferMode.MemoryMappedFile,
        ReuseFileStream = true,
        ShareSourceFile = true,
        RetainDocuments = true,
        // Spawn the worker processes at startup. The first lease then only renders.
        PrewarmWorkers = true,
    };

    public static RenderOptions CreateRenderOptions(int dpi, bool removeRedSeal) =>
        CreateRenderOptions(dpi, removeRedSeal, PdfAntiAliasing.All);

    public static RenderOptions CreateRenderOptions(int dpi, OcrRuntimeConfig config) =>
        CreateRenderOptions(dpi, config.RemoveRedSeal, AntiAliasingOf(config));

    /// <summary>
    /// <c>None</c> maps to PDFium <c>RENDER_NO_SMOOTHIMAGE</c>, which samples a scanned page
    /// nearest-neighbour when it is shrunk to the OCR DPI and drops thin strokes. Smoothing
    /// costs a little CPU per page and nothing on the GPU, so the default keeps it on.
    /// </summary>
    public static PdfAntiAliasing AntiAliasingOf(OcrRuntimeConfig config)
    {
        PdfAntiAliasing flags = PdfAntiAliasing.None;
        if (config.AntiAliasText) flags |= PdfAntiAliasing.Text;
        if (config.AntiAliasImages) flags |= PdfAntiAliasing.Images;
        if (config.AntiAliasPaths) flags |= PdfAntiAliasing.Paths;
        return flags;
    }

    public static RenderOptions CreateRenderOptions(int dpi, bool removeRedSeal, PdfAntiAliasing antiAliasing) =>
        new(
            Dpi: dpi,
            WithAnnotations: false,
            WithFormFill: false,
            AntiAliasing: antiAliasing,
            // Red-seal removal needs the red channel. Gray8 cannot tell a seal from text.
            Grayscale: !removeRedSeal)
        {
            // PDFium renders Gray8 when the switch is off. The parallel path reads those
            // bytes with no BGRA expand. Conversion.ToImages ignores NativeGrayscale, so
            // the in-process fallback stays a bitmap.
            NativeGrayscale = !removeRedSeal,
        };
}
