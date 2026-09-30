using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using PDFtoImage.Parallel;

namespace MiniOcr.Services;

/// <summary>
/// PDFtoImage.Parallel re-executes this process with
/// <c>PDFTOIMAGE_PARALLEL_WORKER_PIPE</c> set. Native AOT enters the worker from
/// that package's module initializer. A normal <c>dotnet run</c> of this project
/// still has the Native AOT feature switches on (<c>IsDynamicCodeSupported</c>
/// and startup hooks both false), so the package skips the startup hook and
/// expects the initializer instead. Referenced-assembly initializers are not
/// guaranteed to run before <c>Main</c> on CoreCLR, which would boot the web
/// host inside the worker. This entry-assembly initializer runs first and calls
/// the same bootstrap.
/// </summary>
internal static class PdfParallelWorkerEntry
{
    [ModuleInitializer]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, "PDFtoImage.Parallel.Internals.WorkerBootstrap", "PDFtoImage.Parallel")]
    internal static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDFTOIMAGE_PARALLEL_WORKER_PIPE")))
            return;

        try
        {
            Type? bootstrap = typeof(ParallelPdfProcessor).Assembly.GetType(
                "PDFtoImage.Parallel.Internals.WorkerBootstrap", throwOnError: false);
            MethodInfo? run = bootstrap?.GetMethod("RunIfWorker", BindingFlags.Static | BindingFlags.NonPublic);
            run?.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
