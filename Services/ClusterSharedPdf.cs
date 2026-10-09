using Microsoft.Extensions.Logging;

namespace MiniOcr.Services;

/// <summary>
/// One PDF file in a directory every same-machine process can read.
/// Workers open that path instead of downloading the bytes again.
/// A path that is missing or the wrong length falls back to the coordinator HTTP copy,
/// which is what a process on another machine does. Container hostnames differ even
/// when they share the volume, so visibility is the file itself, not the hostname.
/// </summary>
public static class ClusterSharedPdf
{
    public static bool IsRemoteUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static bool IsJobId(string? jobId)
    {
        if (jobId is null || jobId.Length != 32)
            return false;
        foreach (char c in jobId)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!hex)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Write <paramref name="pdf"/> as <c>{jobId}.pdf</c> under <paramref name="sharedDir"/>.
    /// Returns the absolute path, or null when sharing is off or the write fails.
    /// </summary>
    public static string? TryWrite(string? sharedDir, string jobId, ReadOnlyMemory<byte> pdf, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(sharedDir) || pdf.Length == 0 || !IsJobId(jobId))
            return null;

        string root;
        try
        {
            root = Path.GetFullPath(sharedDir);
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Cluster shared directory {Dir} is not usable; workers will download the PDF", sharedDir);
            return null;
        }

        string finalPath = Path.Combine(root, jobId + ".pdf");
        string partial = finalPath + ".partial";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 1024 * 1024,
                Options = FileOptions.SequentialScan,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            }

            using (FileStream output = new(partial, options))
                output.Write(pdf.Span);

            File.Move(partial, finalPath, overwrite: true);
            logger?.LogInformation(
                "Cluster job {JobId} PDF shared at {Path} bytes={Bytes}",
                jobId,
                finalPath,
                pdf.Length);
            return finalPath;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to publish shared PDF {Path}; workers will download it", finalPath);
            TryDelete(partial, out _);
            return null;
        }
    }

    /// <summary>
    /// Accept <paramref name="advertised"/> when it is a file this process can see
    /// directly under <paramref name="sharedDir"/> and the length matches.
    /// <c>file://</c> URLs are accepted. Anything else, including a path outside the
    /// directory, returns false so the caller keeps the HTTP download.
    /// </summary>
    public static bool TryResolve(string? sharedDir, string? advertised, long expectedLength, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(sharedDir) || expectedLength <= 0)
            return false;

        string? candidate = NormalizeAdvertised(advertised);
        if (candidate is null)
            return false;

        string root;
        string full;
        try
        {
            root = Path.GetFullPath(sharedDir);
            full = Path.GetFullPath(candidate);
        }
        catch (Exception)
        {
            return false;
        }

        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.Ordinal))
            return false;

        string name = Path.GetFileName(full);
        if (name.Length != 36 || !name.EndsWith(".pdf", StringComparison.Ordinal))
            return false;
        if (!IsJobId(name[..32]))
            return false;

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length != expectedLength)
                return false;
        }
        catch (Exception)
        {
            return false;
        }

        path = full;
        return true;
    }

    /// <summary>Remove a shared PDF after the job ends. Missing files count as success.</summary>
    public static void DeleteWhenDone(string? path, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (TryDelete(path, out string? reason))
        {
            logger?.LogInformation("Deleted shared PDF {Path}", path);
            return;
        }

        logger?.LogDebug("Shared PDF still in use {Path}: {Reason}", path, reason);
        _ = Task.Run(async () =>
        {
            int[] delaysMs = [200, 500, 1000, 2000, 5000, 10000];
            foreach (int delay in delaysMs)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (TryDelete(path, out string? retryReason))
                {
                    logger?.LogInformation("Deleted shared PDF {Path}", path);
                    return;
                }

                logger?.LogDebug("Shared PDF delete retry failed {Path}: {Reason}", path, retryReason);
            }

            logger?.LogWarning("Failed to delete shared PDF {Path}", path);
        });
    }

    /// <summary>Drop shared PDFs and partial writes older than one hour. In-use files stay.</summary>
    public static void SweepExpired(string? sharedDir, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(sharedDir) || !Directory.Exists(sharedDir))
            return;

        try
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (string path in Directory.EnumerateFiles(sharedDir))
            {
                string name = Path.GetFileName(path);
                bool pdf = name.Length == 36 && name.EndsWith(".pdf", StringComparison.Ordinal) && IsJobId(name[..32]);
                bool partial = name.EndsWith(".pdf.partial", StringComparison.Ordinal)
                    && name.Length == 44
                    && IsJobId(name[..32]);
                if (!pdf && !partial)
                    continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(path) > cutoff)
                        continue;
                    if (!TryDelete(path, out string? reason))
                        logger?.LogDebug("Expired shared PDF remains {Path}: {Reason}", path, reason);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug("Expired shared PDF remains {Path}: {Reason}", path, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug("Could not scan shared PDFs: {Reason}", ex.Message);
        }
    }

    private static string? NormalizeAdvertised(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
                return null;
            return uri.LocalPath;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
            return null;
        return trimmed;
    }

    private static bool TryDelete(string path, out string? reason)
    {
        try
        {
            File.Delete(path);
            reason = null;
            return true;
        }
        catch (FileNotFoundException)
        {
            reason = null;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
