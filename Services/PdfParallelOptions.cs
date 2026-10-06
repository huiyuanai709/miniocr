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

    public static RenderOptions CreateRenderOptions(int dpi) =>
        new(
            Dpi: dpi,
            WithAnnotations: false,
            WithFormFill: false,
            AntiAliasing: PdfAntiAliasing.None,
            Grayscale: true)
        {
            // PDFium renders Gray8. The parallel path reads those bytes with no BGRA expand.
            // Conversion.ToImages ignores NativeGrayscale, so the in-process fallback stays a bitmap.
            NativeGrayscale = true,
        };
}
