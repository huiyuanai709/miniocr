using System.Diagnostics;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Contest PDFs that already live on the deploy host. Lookup is read-only:
/// this type never creates, writes, truncates, moves, renames, or deletes
/// anything under the resource directory.
/// </summary>
public static class ChallengeResourceFiles
{
    public const string EnvVar = "MINIOCR_CHALLENGE_RESOURCE_DIR";
    public const string DownloadMode = "local-resource";

    public enum SourceKind
    {
        /// <summary>Download the absolute http(s) URL. Resource dir is unset, or the local file is missing and a URL is present.</summary>
        Remote,
        /// <summary>Open the file under the resource directory. Do not download.</summary>
        Local,
        /// <summary>No local file and no usable URL. The caller records empty pages.</summary>
        Missing,
    }

    public readonly record struct SourceDecision(
        SourceKind Kind,
        string FileId,
        string? Url,
        string? LocalPath);

    public readonly record struct LocalRead(RentedBuffer Buffer, string Mode, double ElapsedMs);

    /// <summary>
    /// Empty or whitespace stays unset (keep URL download). A non-empty path is made absolute.
    /// Does not create the directory and does not check that it exists.
    /// </summary>
    public static string? NormalizeRoot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            return Path.GetFullPath(raw.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? FirstConfigured(string? envValue, string? fileValue) =>
        NormalizeRoot(FirstNonEmpty(envValue, fileValue));

    /// <summary>
    /// <paramref name="fileId"/> <c>f1</c> becomes <c>{root}/f1.pdf</c>.
    /// A name that already ends in <c>.pdf</c> is kept. Any other extension is kept as-is.
    /// Rejects empty names, separators, and <c>..</c>. Does not touch the filesystem.
    /// </summary>
    public static bool TryCandidatePath(string? root, string? fileId, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fileId))
            return false;

        string name = fileId.Trim();
        if (name.Length is 0 or > 180)
            return false;
        if (name is "." or "..")
            return false;
        if (name.Contains("..", StringComparison.Ordinal))
            return false;
        if (name.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            return false;
        foreach (char c in name)
        {
            if (char.IsControl(c))
                return false;
        }
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            return false;

        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(name).Length == 0)
        {
            name += ".pdf";
        }

        string rootFull;
        string full;
        try
        {
            rootFull = Path.GetFullPath(root);
            full = Path.GetFullPath(Path.Combine(rootFull, name));
        }
        catch (Exception)
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !PathsEqual(parent, rootFull))
            return false;

        path = full;
        return true;
    }

    /// <summary>
    /// Keep a file when <paramref name="resourceRoot"/> is unset only if it has <c>fileId</c> and an
    /// absolute http(s) URL. When the root is set, <c>fileId</c> alone is enough; a non-http URL is dropped.
    /// </summary>
    public static bool TryAcceptFile(string? resourceRoot, ChallengeFileRef file, out ChallengeFileRef normalized)
    {
        normalized = file;
        string? fileId = file.FileId?.Trim();
        if (string.IsNullOrEmpty(fileId))
            return false;

        string? url = NormalizeHttpUrl(file.Url);
        if (string.IsNullOrEmpty(resourceRoot))
        {
            if (url is null)
                return false;
            normalized = new ChallengeFileRef { FileId = fileId, Url = url };
            return true;
        }

        normalized = new ChallengeFileRef { FileId = fileId, Url = url };
        return true;
    }

    public static string? NormalizeHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        string trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return trimmed;
    }

    /// <summary>
    /// Local file wins when <paramref name="resourceRoot"/> is set and the PDF is there.
    /// A missing local file falls back to an absolute http(s) URL when one is present.
    /// Existence is a read-only check.
    /// </summary>
    public static SourceDecision Decide(string? resourceRoot, string? fileId, string? url)
    {
        string id = fileId?.Trim() ?? "";
        string? http = NormalizeHttpUrl(url);
        if (string.IsNullOrEmpty(resourceRoot))
        {
            return http is null
                ? new SourceDecision(SourceKind.Missing, id, null, null)
                : new SourceDecision(SourceKind.Remote, id, http, null);
        }

        if (!TryCandidatePath(resourceRoot, id, out string candidate))
        {
            return http is null
                ? new SourceDecision(SourceKind.Missing, id, null, null)
                : new SourceDecision(SourceKind.Remote, id, http, null);
        }

        if (ExistsReadOnly(candidate))
            return new SourceDecision(SourceKind.Local, id, http, candidate);

        return http is null
            ? new SourceDecision(SourceKind.Missing, id, null, candidate)
            : new SourceDecision(SourceKind.Remote, id, http, candidate);
    }

    /// <summary>
    /// Read a contest PDF into a rented buffer. Opens the file for read only.
    /// </summary>
    public static LocalRead ReadLocal(string path)
    {
        Stopwatch sw = Stopwatch.StartNew();
        RentedBuffer buffer = LocalPdfFile.ReadAsRented(path);
        sw.Stop();
        return new LocalRead(buffer, DownloadMode, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Workers may open <paramref name="advertised"/> when it is an existing direct child of the
    /// resource root and the length matches. Does not copy the file and does not create directories.
    /// </summary>
    public static bool TryAdoptForCluster(string? resourceRoot, string? advertised, long expectedLength, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(resourceRoot) || expectedLength <= 0 || string.IsNullOrWhiteSpace(advertised))
            return false;

        string raw = advertised.Trim();
        if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
                return false;
            raw = uri.LocalPath;
        }
        else if (raw.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        string rootFull;
        string full;
        try
        {
            rootFull = Path.GetFullPath(resourceRoot);
            full = Path.GetFullPath(raw);
        }
        catch (Exception)
        {
            return false;
        }

        string name = Path.GetFileName(full);
        if (!TryCandidatePath(rootFull, name, out string expected) || !PathsEqual(full, expected))
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

    public static string RequiredFieldsError(bool resourceMode) =>
        resourceMode
            ? "Required: key, callbackUrl, files[] (each with fileId; url optional)."
            : "Required: key, callbackUrl, files[] (each with fileId + url).";

    public static string MissingFilesError(bool resourceMode) =>
        resourceMode
            ? "No valid files (need fileId; url is optional when the challenge resource directory is set)."
            : "No valid files (need fileId + http(s) url).";

    private static bool ExistsReadOnly(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a,
            b,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? FirstNonEmpty(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a))
            return a.Trim();
        if (!string.IsNullOrWhiteSpace(b))
            return b.Trim();
        return null;
    }
}

/// <summary>Resolved contest resource directory. Empty means keep URL download.</summary>
public sealed class ChallengeRuntimeConfig
{
    public string ResourceDir { get; init; } = "";

    public bool Enabled => ResourceDir.Length > 0;

    /// <summary>
    /// Env <see cref="ChallengeResourceFiles.EnvVar"/> wins over <c>challenge.resourceDir</c>.
    /// Both empty keeps URL download. Does not create the directory.
    /// </summary>
    public static ChallengeRuntimeConfig Resolve(AppConfigFile? file, Func<string, string?>? env = null)
    {
        env ??= static name => Environment.GetEnvironmentVariable(name);
        string? root = ChallengeResourceFiles.FirstConfigured(
            env(ChallengeResourceFiles.EnvVar),
            file?.Challenge?.ResourceDir);
        return new ChallengeRuntimeConfig { ResourceDir = root ?? "" };
    }
}
