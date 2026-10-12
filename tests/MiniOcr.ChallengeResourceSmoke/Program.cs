using MiniOcr.Models;
using MiniOcr.Services;

int failed = 0;

void AssertTrue(bool cond, string msg)
{
    if (cond)
    {
        Console.WriteLine("  PASS  " + msg);
        return;
    }

    Console.WriteLine("  FAIL  " + msg);
    failed++;
}

void AssertEqual(string expected, string actual, string msg)
{
    AssertTrue(
        string.Equals(expected, actual, StringComparison.Ordinal),
        msg + $" (expected '{expected}', got '{actual}')");
}

Console.WriteLine("=== challenge resource dir ===");

AssertTrue(ChallengeResourceFiles.NormalizeRoot(null) is null, "unset root stays unset");
AssertTrue(ChallengeResourceFiles.NormalizeRoot("   ") is null, "blank root stays unset");

ChallengeRuntimeConfig off = ChallengeRuntimeConfig.Resolve(new AppConfigFile(), _ => null);
AssertTrue(!off.Enabled, "empty config keeps URL download");

string fromFile = Path.GetFullPath("/tmp/miniocr-from-file");
ChallengeRuntimeConfig fileOnly = ChallengeRuntimeConfig.Resolve(
    new AppConfigFile { Challenge = new ChallengeFileConfig { ResourceDir = fromFile } },
    _ => null);
AssertEqual(fromFile, fileOnly.ResourceDir, "challenge.resourceDir is used when env is unset");
string missingRoot = Path.GetFullPath("/tmp/miniocr-resource-missing-" + Guid.NewGuid().ToString("N"));
ChallengeRuntimeConfig missingDir = ChallengeRuntimeConfig.Resolve(
    new AppConfigFile { Challenge = new ChallengeFileConfig { ResourceDir = missingRoot } },
    _ => null);
AssertEqual(missingRoot, missingDir.ResourceDir, "a missing directory is still configured");
AssertTrue(!Directory.Exists(missingRoot), "resolve does not create the resource directory");

ChallengeRuntimeConfig envWins = ChallengeRuntimeConfig.Resolve(
    new AppConfigFile { Challenge = new ChallengeFileConfig { ResourceDir = fromFile } },
    name => name == ChallengeResourceFiles.EnvVar ? "/tmp/miniocr-from-env" : null);
AssertEqual(Path.GetFullPath("/tmp/miniocr-from-env"), envWins.ResourceDir, "MINIOCR_CHALLENGE_RESOURCE_DIR overrides the file");

ChallengeRuntimeConfig blankEnv = ChallengeRuntimeConfig.Resolve(
    new AppConfigFile { Challenge = new ChallengeFileConfig { ResourceDir = fromFile } },
    name => name == ChallengeResourceFiles.EnvVar ? "  " : null);
AssertEqual(fromFile, blankEnv.ResourceDir, "blank env does not hide the file value");

AssertTrue(
    !ChallengeResourceFiles.TryAcceptFile(null, new ChallengeFileRef { FileId = "f1" }, out _),
    "without a resource dir, fileId alone is rejected");
AssertTrue(
    ChallengeResourceFiles.TryAcceptFile(
        null,
        new ChallengeFileRef { FileId = " f1 ", Url = " https://files.example/a.pdf " },
        out ChallengeFileRef httpFile) &&
    httpFile.FileId == "f1" &&
    httpFile.Url == "https://files.example/a.pdf",
    "without a resource dir, fileId + http(s) url is kept");
AssertTrue(
    !ChallengeResourceFiles.TryAcceptFile(
        null,
        new ChallengeFileRef { FileId = "f1", Url = "ftp://files.example/a.pdf" },
        out _),
    "without a resource dir, a non-http url is rejected");

string root = Path.Combine(Path.GetTempPath(), "miniocr-resource-" + Guid.NewGuid().ToString("N"));
string shared = Path.Combine(Path.GetTempPath(), "miniocr-shared-" + Guid.NewGuid().ToString("N"));
string outsideDir = Path.Combine(Path.GetTempPath(), "miniocr-outside-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Directory.CreateDirectory(shared);
Directory.CreateDirectory(outsideDir);
string pdfPath = Path.Combine(root, "f1.pdf");
byte[] pdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"u8.ToArray();
File.WriteAllBytes(pdfPath, pdf);
string outside = Path.Combine(outsideDir, "secret.pdf");
File.WriteAllBytes(outside, pdf);
byte[] beforeBytes = File.ReadAllBytes(pdfPath);
DateTime beforeWrite = File.GetLastWriteTimeUtc(pdfPath);
string[] beforeNames = Directory.GetFiles(root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
string[] sharedBefore = Directory.GetFiles(shared);

try
{
    AssertTrue(
        ChallengeResourceFiles.TryAcceptFile(root, new ChallengeFileRef { FileId = "f1" }, out ChallengeFileRef idOnly) &&
        idOnly.FileId == "f1" &&
        idOnly.Url is null,
        "with a resource dir, fileId alone is accepted");
    AssertTrue(
        ChallengeResourceFiles.TryAcceptFile(
            root,
            new ChallengeFileRef { FileId = "f2", Url = "not-a-url" },
            out ChallengeFileRef dropped) &&
        dropped.Url is null,
        "a non-http url is dropped and the fileId is still accepted");

    AssertTrue(
        ChallengeResourceFiles.TryCandidatePath(root, "f1", out string candidate) &&
        candidate == pdfPath,
        "fileId f1 maps to f1.pdf");
    AssertTrue(
        ChallengeResourceFiles.TryCandidatePath(root, "f1.pdf", out string already) &&
        already == pdfPath,
        "fileId f1.pdf is not doubled");
    AssertTrue(!ChallengeResourceFiles.TryCandidatePath(root, "../secret", out _), "fileId ../secret is rejected");
    AssertTrue(!ChallengeResourceFiles.TryCandidatePath(root, "..\\secret", out _), "fileId ..\\secret is rejected");
    AssertTrue(!ChallengeResourceFiles.TryCandidatePath(root, "nested/f1", out _), "fileId nested/f1 is rejected");
    AssertTrue(!ChallengeResourceFiles.TryCandidatePath(root, "f1\nf2", out _), "fileId with a newline is rejected");
    AssertTrue(!ChallengeResourceFiles.TryCandidatePath(root, outside, out _), "an absolute path fileId is rejected");

    ChallengeResourceFiles.SourceDecision local = ChallengeResourceFiles.Decide(root, "f1", null);
    AssertTrue(local.Kind == ChallengeResourceFiles.SourceKind.Local, "existing f1.pdf is local-resource");
    AssertEqual(pdfPath, local.LocalPath ?? "", "local path is the contest file");

    ChallengeResourceFiles.SourceDecision fallback = ChallengeResourceFiles.Decide(
        root, "f2", "https://files.example/f2.pdf");
    AssertTrue(fallback.Kind == ChallengeResourceFiles.SourceKind.Remote, "missing local file with http url falls back");
    AssertTrue(fallback.LocalPath is not null && fallback.LocalPath.EndsWith("f2.pdf", StringComparison.Ordinal),
        "fallback still records the path that was missing");

    ChallengeResourceFiles.SourceDecision missing = ChallengeResourceFiles.Decide(root, "f3", null);
    AssertTrue(missing.Kind == ChallengeResourceFiles.SourceKind.Missing, "missing local file without url is empty pages");

    ChallengeResourceFiles.SourceDecision remoteOnly = ChallengeResourceFiles.Decide(
        null, "f1", "https://files.example/f1.pdf");
    AssertTrue(remoteOnly.Kind == ChallengeResourceFiles.SourceKind.Remote && remoteOnly.LocalPath is null,
        "unset resource dir keeps URL download and does not invent a path");

    ChallengeResourceFiles.LocalRead read = ChallengeResourceFiles.ReadLocal(pdfPath);
    using (read.Buffer)
    {
        AssertEqual(ChallengeResourceFiles.DownloadMode, read.Mode, "downloadMode is local-resource");
        AssertTrue(read.Buffer.Length == pdf.Length, "read length matches the file");
        AssertTrue(read.Buffer.Span.SequenceEqual(pdf), "read bytes match the file");
    }

    AssertTrue(
        ChallengeResourceFiles.TryAdoptForCluster(root, pdfPath, pdf.Length, out string adopted) &&
        adopted == pdfPath,
        "cluster adopts the contest path");
    AssertTrue(
        !ChallengeResourceFiles.TryAdoptForCluster(root, pdfPath, pdf.Length + 1, out _),
        "length mismatch does not adopt");
    AssertTrue(
        !ChallengeResourceFiles.TryAdoptForCluster(root, outside, pdf.Length, out _),
        "a file outside the resource dir is not adopted");
    AssertTrue(
        !ChallengeResourceFiles.TryAdoptForCluster(root, "https://files.example/f1.pdf", pdf.Length, out _),
        "an http url is not treated as a local contest file");
    AssertTrue(
        !ChallengeResourceFiles.TryAdoptForCluster(null, pdfPath, pdf.Length, out _),
        "unset resource dir does not adopt a local path");

    byte[] afterBytes = File.ReadAllBytes(pdfPath);
    string[] afterNames = Directory.GetFiles(root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
    AssertTrue(beforeBytes.AsSpan().SequenceEqual(afterBytes), "contest PDF bytes are unchanged");
    AssertTrue(beforeNames.SequenceEqual(afterNames), "resource directory gained no files");
    AssertTrue(File.GetLastWriteTimeUtc(pdfPath) == beforeWrite, "contest PDF mtime is unchanged");
    AssertTrue(Directory.GetFiles(shared).SequenceEqual(sharedBefore), "shared dir was not written");
    AssertTrue(File.ReadAllBytes(outside).AsSpan().SequenceEqual(pdf), "outside file was not touched");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    try { Directory.Delete(shared, recursive: true); } catch (IOException) { }
    try { Directory.Delete(outsideDir, recursive: true); } catch (IOException) { }
}

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    return 1;
}

Console.WriteLine("OK");
return 0;
