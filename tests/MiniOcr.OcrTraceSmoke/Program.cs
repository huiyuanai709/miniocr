using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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

using ILoggerFactory logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
ILogger logger = logs.CreateLogger("ocr-trace");

Console.WriteLine("=== write url and truncated page text ===");
string dir = Path.Combine(Path.GetTempPath(), "miniocr-trace-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
string logPath = Path.Combine(dir, "ocr-trace.jsonl");
OcrTraceLog trace = OcrTraceLog.Create(
    logger,
    env: name => name switch
    {
        OcrTraceLog.PathEnv => logPath,
        OcrTraceLog.MaxCharsEnv => "8",
        _ => null,
    },
    directory: dir);
await trace.StartAsync(CancellationToken.None);
const string requestId = "req-trace-1";
const string fileUrl =
    "https://user:s3cr3t-key@files.example/a.pdf?file=a.pdf&X-Amz-Signature=super-secret&ttl=60#page=2";
trace.Url(
    "challenge",
    requestId,
    fileUrl,
    fileId: "f1",
    teamId: 9);
string body = "01234567TAILMARKER";
const string textUrl = "https://user:s3cr3t-key@files.example/a.pdf?key=challenge-secret";
trace.Text(
    "challenge",
    requestId,
    "f1",
    textUrl,
    pageCount: 2,
    pages:
    [
        new OcrTracePage(1, "ocr", body),
        new OcrTracePage(2, "textLayer", "第二页识别文本"),
    ],
    teamId: 9,
    downloadMode: "parallel");
const string jobUrl = "https://files.example/a.pdf?sig=super-secret";
using (OcrTraceLog.BeginScope(requestId, "challenge", "f1", 9))
    trace.ClusterJob("job-abc", jobUrl, 2);
AssertTrue(await trace.DrainAsync(TimeSpan.FromSeconds(5)), "background writer drained");

string raw = await File.ReadAllTextAsync(logPath);
AssertTrue(raw.Contains("s3cr3t-key", StringComparison.Ordinal), "userinfo is kept");
AssertTrue(raw.Contains("super-secret", StringComparison.Ordinal), "query signature is kept");
AssertTrue(raw.Contains("challenge-secret", StringComparison.Ordinal), "query key value is kept");
AssertTrue(!raw.Contains("TAILMARKER", StringComparison.Ordinal), "page text is truncated");
AssertTrue(raw.Contains("第二页识别文本", StringComparison.Ordinal), "short page text is kept");
AssertTrue(!raw.Contains('\r'), "records are LF JSONL");

string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
AssertTrue(lines.Length == 5, "url + ocr + 2 pages + job, got " + lines.Length);
using (JsonDocument url = JsonDocument.Parse(lines[0]))
using (JsonDocument ocr = JsonDocument.Parse(lines[1]))
using (JsonDocument page1 = JsonDocument.Parse(lines[2]))
using (JsonDocument job = JsonDocument.Parse(lines[4]))
{
    AssertTrue(url.RootElement.GetProperty("kind").GetString() == "url", "first record is the file URL");
    AssertTrue(url.RootElement.GetProperty("entry").GetString() == "challenge", "entry is challenge");
    AssertTrue(url.RootElement.GetProperty("requestId").GetString() == requestId, "url request id");
    AssertTrue(url.RootElement.GetProperty("fileId").GetString() == "f1", "url file id");
    AssertTrue(url.RootElement.GetProperty("teamId").GetInt32() == 9, "team id kept, key is not a field");
    AssertTrue(url.RootElement.GetProperty("url").GetString() == fileUrl, "file url is the original string");
    AssertTrue(!url.RootElement.TryGetProperty("key", out _), "challenge key is not a log field");
    AssertTrue(ocr.RootElement.GetProperty("url").GetString() == textUrl, "ocr line keeps the original file url");
    AssertTrue(ocr.RootElement.GetProperty("requestId").GetString() == requestId, "ocr request id matches the url");
    AssertTrue(ocr.RootElement.GetProperty("fileId").GetString() == "f1", "ocr file id matches the url");
    AssertTrue(ocr.RootElement.GetProperty("loggedPages").GetInt32() == 2, "both pages are logged");
    AssertTrue(ocr.RootElement.GetProperty("maxChars").GetInt32() == 8, "max chars recorded");
    AssertTrue(page1.RootElement.GetProperty("page").GetInt32() == 1, "page number");
    AssertTrue(page1.RootElement.GetProperty("chars").GetInt32() == body.Length, "original char count");
    AssertTrue(page1.RootElement.GetProperty("truncated").GetBoolean(), "truncated flag");
    AssertTrue(page1.RootElement.GetProperty("text").GetString() == "01234567", "prefix kept");
    AssertTrue(page1.RootElement.GetProperty("requestId").GetString() == requestId, "page request id");
    AssertTrue(job.RootElement.GetProperty("kind").GetString() == "job", "cluster job link");
    AssertTrue(job.RootElement.GetProperty("jobId").GetString() == "job-abc", "cluster job id");
    AssertTrue(job.RootElement.GetProperty("requestId").GetString() == requestId, "job joins the http request");
    AssertTrue(job.RootElement.GetProperty("url").GetString() == jobUrl, "job url is the original string");
}

await trace.StopAsync(CancellationToken.None);
trace.Dispose();

Console.WriteLine("=== full queue does not block ===");
string dropDir = Path.Combine(Path.GetTempPath(), "miniocr-trace-drop-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dropDir);
OcrTraceLog blocked = OcrTraceLog.Create(
    logger,
    env: name => name switch
    {
        OcrTraceLog.QueueEnv => "2",
        OcrTraceLog.PathEnv => Path.Combine(dropDir, "ocr-trace.jsonl"),
        _ => null,
    },
    directory: dropDir);
var sw = Stopwatch.StartNew();
for (int i = 0; i < 20_000; i++)
{
    blocked.Url("ocr", "r", "https://files.example/a.pdf?file=" + i, fileId: "f1");
}

sw.Stop();
Console.WriteLine(
    $"  enqueue 20000 urls with a full queue and no writer: {sw.ElapsedMilliseconds} ms, dropped={blocked.Dropped}, capacity={blocked.QueueCapacity}, enabled={blocked.Enabled}");
AssertTrue(blocked.Dropped >= 19_998, "overflow is dropped instead of waiting");
AssertTrue(sw.ElapsedMilliseconds < 1500, "enqueue stays off disk (" + sw.ElapsedMilliseconds + " ms)");
blocked.Dispose();

Console.WriteLine("=== writer failure does not throw ===");
string badDir = Path.Combine(Path.GetTempPath(), "miniocr-trace-bad-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(badDir);
string blocker = Path.Combine(badDir, "not-a-directory");
await File.WriteAllTextAsync(blocker, "x");
OcrTraceLog broken = OcrTraceLog.Create(
    logger,
    env: name => name == OcrTraceLog.PathEnv ? Path.Combine(blocker, "ocr-trace.jsonl") : null,
    directory: badDir);
await broken.StartAsync(CancellationToken.None);
Exception? thrown = null;
try
{
    broken.Url("ocr", "r", "https://files.example/a.pdf", fileId: "f1");
    broken.Text("ocr", "r", "f1", null, 1, [new OcrTracePage(1, "ocr", "hello")]);
}
catch (Exception ex)
{
    thrown = ex;
}

AssertTrue(thrown is null, "record methods swallow writer setup failure");
AssertTrue(await broken.DrainAsync(TimeSpan.FromSeconds(5)), "flush still completes when the file cannot be opened");
AssertTrue(broken.Dropped > 0, "events are counted as dropped");
await broken.StopAsync(CancellationToken.None);
broken.Dispose();

Console.WriteLine("=== disabled trace does not create a file ===");
string offDir = Path.Combine(Path.GetTempPath(), "miniocr-trace-off-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(offDir);
OcrTraceLog off = OcrTraceLog.Create(
    logger,
    env: name => name == OcrTraceLog.EnabledEnv ? "0" : null,
    directory: offDir);
AssertTrue(!off.Enabled, "MINIOCR_OCR_TRACE=0");
await off.StartAsync(CancellationToken.None);
off.Url("challenge", "r", "https://files.example/secret-path.pdf?token=abc", fileId: "f1");
off.Text("challenge", "r", "f1", "https://files.example/secret-path.pdf", 1, [new OcrTracePage(1, "ocr", "should-not-land")]);
AssertTrue(await off.DrainAsync(), "disabled drain is a no-op");
AssertTrue(!File.Exists(Path.Combine(offDir, OcrTraceLog.FileName)), "disabled trace creates no file");
AssertTrue(off.Dropped == 0, "disabled calls are not counted as drops");
await off.StopAsync(CancellationToken.None);
off.Dispose();

try
{
    Directory.Delete(dir, recursive: true);
    Directory.Delete(dropDir, recursive: true);
    Directory.Delete(badDir, recursive: true);
    Directory.Delete(offDir, recursive: true);
}
catch
{
    // Temp leftovers are fine.
}

if (failed > 0)
{
    Console.WriteLine("FAILED " + failed);
    return 1;
}

Console.WriteLine("OK");
return 0;
