using System.Buffers;

namespace MiniOcr.Services;

/// <summary>
/// Reads a local PDF. On Windows the path is opened with the <c>\\?\</c> prefix so
/// non-ASCII names (and paths longer than MAX_PATH) reach the Unicode Win32 APIs.
/// </summary>
public static class LocalPdfFile
{
    public const int MaxBytes = 300 * 1024 * 1024;

    public static byte[] ReadAllBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("PDF path is empty.", nameof(path));

        string full = Path.GetFullPath(path);
        string openPath = ToExtendedPath(full, OperatingSystem.IsWindows());
        if (!File.Exists(openPath))
            throw new FileNotFoundException("PDF not found: " + full, full);

        long length = new FileInfo(openPath).Length;
        if (length > MaxBytes)
            throw new InvalidOperationException($"PDF is {length} bytes; max supported is {MaxBytes}.");
        if (length <= 0)
            throw new InvalidOperationException("PDF is empty: " + full);

        byte[] bytes = File.ReadAllBytes(openPath);
        if (!LooksLikePdf(bytes))
            throw new InvalidOperationException("File is not a PDF (missing %PDF header): " + full);
        return bytes;
    }

    /// <summary>
    /// Reads straight into a pooled buffer: one copy from the page cache, no intermediate
    /// <c>byte[]</c> the size of the PDF for the GC to clear afterwards.
    /// </summary>
    public static RentedBuffer ReadAsRented(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("PDF path is empty.", nameof(path));

        string full = Path.GetFullPath(path);
        string openPath = ToExtendedPath(full, OperatingSystem.IsWindows());
        if (!File.Exists(openPath))
            throw new FileNotFoundException("PDF not found: " + full, full);

        using FileStream stream = new(
            openPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1,
            FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > MaxBytes)
            throw new InvalidOperationException($"PDF is {length} bytes; max supported is {MaxBytes}.");
        if (length <= 0)
            throw new InvalidOperationException("PDF is empty: " + full);

        int size = (int)length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int read = 0;
            while (read < size)
            {
                int n = stream.Read(rented, read, size - read);
                if (n <= 0)
                    break;
                read += n;
            }

            if (read != size)
                throw new IOException($"PDF read short: {read} of {size} bytes from {full}");
            if (!LooksLikePdf(rented.AsSpan(0, size)))
                throw new InvalidOperationException("File is not a PDF (missing %PDF header): " + full);
            RentedBuffer result = new(rented, size, ArrayPool<byte>.Shared);
            rented = null!;
            return result;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static RentedBuffer RentCopy(byte[] bytes)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(bytes.Length);
        bytes.AsSpan().CopyTo(rented);
        return new RentedBuffer(rented, bytes.Length, ArrayPool<byte>.Shared);
    }

    public static bool LooksLikePdf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 5 &&
        bytes[0] == (byte)'%' &&
        bytes[1] == (byte)'P' &&
        bytes[2] == (byte)'D' &&
        bytes[3] == (byte)'F';

    /// <summary>
    /// <paramref name="windows"/> is explicit so tests can check the prefix on Linux.
    /// <paramref name="fullPath"/> must already be absolute.
    /// </summary>
    public static string ToExtendedPath(string fullPath, bool windows)
    {
        if (!windows)
            return fullPath;
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }
}
