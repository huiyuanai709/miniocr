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
    AssertTrue(string.Equals(expected, actual, StringComparison.Ordinal),
        msg + $" (expected '{expected}', got '{actual}')");
}

static bool SameBytes(ReadOnlySpan<byte> actual, byte[] expected)
{
    if (actual.Length != expected.Length)
        return false;
    for (int i = 0; i < expected.Length; i++)
    {
        if (actual[i] != expected[i])
            return false;
    }

    return true;
}

Console.WriteLine("=== WeChat OCR protobuf ===");

byte[] wx3 = WeChatProtobuf.EncodeWx3Request(2, "a.png");
AssertTrue(SameBytes(wx3, [0x08, 0x00, 0x10, 0x02, 0x1A, 0x07, 0x0A, 0x05, 0x61, 0x2E, 0x70, 0x6E, 0x67]),
    "wx3 request bytes for a.png task 2");

string chinesePath = "C:\\Users\\mafuz\\Desktop\\合同.png";
byte[] wx4 = WeChatProtobuf.EncodeWx4Request(2, chinesePath);
AssertTrue(wx4.AsSpan().IndexOf("合同"u8) >= 0, "wx4 request keeps UTF-8 Chinese path");
byte[] rt = [0x08, 0x01, 0x10, 0x01, 0x18, 0x00];
AssertTrue(wx4.AsSpan().IndexOf(rt) >= 0, "wx4 ReqType t1=1 t2=1 t3=0");

// Hand-built wx4 response, independent of the encoder: text "Hi", 100x50, task 2, err 0.
byte[] wx4Resp =
[
    0x08, 0x02, 0x10, 0x00, 0x1A, 0x0A,
    0x1A, 0x04, 0x12, 0x02, 0x48, 0x69,
    0x20, 0x64, 0x28, 0x32,
];
WeChatPush parsed4 = WeChatProtobuf.ParsePush(WeChatOcrKind.Wx4, WeChatProtobuf.Wx4Response, wx4Resp);
AssertTrue(parsed4 is WeChatPush.Result { TaskId: 2, ErrCode: 0, Width: 100, Height: 50 },
    "wx4 response header");
AssertEqual("Hi", WeChatProtobuf.JoinLines(((WeChatPush.Result)parsed4).Lines), "wx4 line text");

byte[] wx4Zh =
[
    0x08, 0x02, 0x10, 0x00, 0x1A, 0x07,
    0x1A, 0x05, 0x12, 0x03, 0xE4, 0xBD, 0xA0,
];
WeChatPush parsedZh = WeChatProtobuf.ParsePush(WeChatOcrKind.Wx4, WeChatProtobuf.Wx4Response, wx4Zh);
AssertEqual("你", WeChatProtobuf.JoinLines(((WeChatPush.Result)parsedZh).Lines), "wx4 Chinese line");

AssertTrue(
    WeChatProtobuf.ParsePush(WeChatOcrKind.Wx4, WeChatProtobuf.Wx4Handshake, [0x08, 0x01]) is WeChatPush.Handshake { Ok: true },
    "wx4 handshake supported");
AssertTrue(
    WeChatProtobuf.ParsePush(WeChatOcrKind.Wx4, WeChatProtobuf.Wx4Handshake, [0x08, 0x00]) is WeChatPush.Handshake { Ok: false },
    "wx4 handshake not supported");

// wx3 init type=1 err=0, then a result whose line text lives only on char blocks.
AssertTrue(
    WeChatProtobuf.ParsePush(WeChatOcrKind.Wx3, WeChatProtobuf.Wx3Push, [0x08, 0x01, 0x10, 0x01, 0x18, 0x00]) is WeChatPush.Handshake { Ok: true },
    "wx3 init handshake");

// OcrRespond type=0 task=2 err=0, output line with empty text and one block "甲".
// line: field 4 (block) = OCRResultChar field 2 string E7 94 B2 ("甲")
// block: 12 03 E7 94 B2
// line: 22 05 12 03 E7 94 B2   (field 4, wire 2)
// output field 1: 0A 07 22 05 12 03 E7 94 B2
// respond field 4: 22 09 0A 07 ...
byte[] wx3Blocks =
[
    0x08, 0x00, 0x10, 0x02, 0x18, 0x00,
    0x22, 0x09,
    0x0A, 0x07,
    0x22, 0x05, 0x12, 0x03, 0xE7, 0x94, 0xB2,
];
WeChatPush parsedBlocks = WeChatProtobuf.ParsePush(WeChatOcrKind.Wx3, WeChatProtobuf.Wx3Push, wx3Blocks);
AssertEqual("甲", WeChatProtobuf.JoinLines(((WeChatPush.Result)parsedBlocks).Lines), "wx3 char-block fallback");

AssertEqual("第一行\n第二行", WeChatProtobuf.JoinLines([" 第一行 ", "", "第二行"]), "join trims and skips blanks");
AssertTrue(
    WeChatProtobuf.ParsePush(WeChatOcrKind.Wx4, 20012, [0x08, 0x01]) is WeChatPush.Ignored,
    "unknown wx4 request id ignored");

Console.WriteLine("=== WeChat OCR locator ===");

string root = Path.Combine(Path.GetTempPath(), "miniocr-wechat-" + Guid.NewGuid().ToString("N"));
string appData = Path.Combine(root, "app");
string pf = Path.Combine(root, "pf");
string pf86 = Path.Combine(root, "pf86");
try
{
    string wx4Dll = Path.Combine(appData, "Tencent", "xwechat", "XPlugin", "plugins", "WeChatOcr", "8011", "extracted", "wxocr.dll");
    string wx3Exe = Path.Combine(appData, "Tencent", "WeChat", "XPlugin", "Plugins", "WeChatOCR", "7079", "extracted", "WeChatOCR.exe");
    string versionDir = Path.Combine(pf, "Tencent", "Weixin", "4.1.2.3");
    string weixin = Path.Combine(pf, "Tencent", "Weixin", "weixin.exe");
    string mmmojo4 = Path.Combine(versionDir, "mmmojo_64.dll");
    string wx3Dir = Path.Combine(pf86, "Tencent", "WeChat", "[3.9.10.19]");
    string mmmojo3 = Path.Combine(wx3Dir, "mmmojo_64.dll");

    Directory.CreateDirectory(Path.GetDirectoryName(wx4Dll)!);
    Directory.CreateDirectory(Path.GetDirectoryName(wx3Exe)!);
    Directory.CreateDirectory(versionDir);
    Directory.CreateDirectory(wx3Dir);
    File.WriteAllBytes(wx4Dll, [1]);
    File.WriteAllBytes(wx3Exe, [2]);
    File.WriteAllBytes(weixin, [3]);
    File.WriteAllBytes(mmmojo4, [4]);
    File.WriteAllBytes(mmmojo3, [5]);
    File.SetLastWriteTimeUtc(wx4Dll, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
    File.SetLastWriteTimeUtc(wx3Exe, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));

    var probe = FileSystemWeChatProbe.Instance;
    var both = new WeChatLocateInput { AppData = appData, ProgramFiles = pf, ProgramFilesX86 = pf86 };
    WeChatOcrLocation found = WeChatOcrLocator.Locate(both, probe);
    Console.WriteLine(found.Report);
    AssertTrue(found.Found, "layout with 3.x and 4.x is found");
    AssertTrue(found.Kind == WeChatOcrKind.Wx4, "prefers wx4 when both exist");
    AssertEqual(wx4Dll, found.PluginPath, "wx4 plugin path");
    AssertEqual(versionDir, found.WeChatDir, "wx4 version dir");
    AssertEqual(weixin, found.LaunchExe, "wx4 launches weixin.exe");
    AssertTrue(found.Report.Contains("selected kind=wx4", StringComparison.Ordinal), "report names the selection");

    File.Delete(weixin);
    WeChatOcrLocation only3 = WeChatOcrLocator.Locate(both, probe);
    Console.WriteLine(only3.Report);
    AssertTrue(only3.Found && only3.Kind == WeChatOcrKind.Wx3, "falls through to wx3 when weixin.exe is missing");
    AssertEqual(wx3Exe, only3.LaunchExe, "wx3 launches WeChatOCR.exe");
    AssertEqual(wx3Dir, only3.WeChatDir, "wx3 bracket version dir");

    File.WriteAllBytes(weixin, [3]);
    var pinned = new WeChatLocateInput
    {
        AppData = appData,
        ProgramFiles = pf,
        ProgramFilesX86 = pf86,
        PluginPath = wx3Exe,
        WeChatDir = wx3Dir,
    };
    WeChatOcrLocation explicitHit = WeChatOcrLocator.Locate(pinned, probe);
    AssertTrue(explicitHit.Found && explicitHit.Kind == WeChatOcrKind.Wx3, "explicit 3.x paths win over newer 4.x");
    AssertEqual(wx3Exe, explicitHit.PluginPath, "explicit plugin kept");

    var missing = new WeChatLocateInput
    {
        AppData = appData,
        ProgramFiles = pf,
        PluginPath = Path.Combine(root, "nope", "wxocr.dll"),
    };
    WeChatOcrLocation miss = WeChatOcrLocator.Locate(missing, probe);
    AssertTrue(!miss.Found, "missing configured plugin does not silently switch");
    AssertTrue(miss.Report.Contains("NOT FOUND", StringComparison.Ordinal), "missing plugin report is explicit");

    var fromRegistry = new WeChatLocateInput
    {
        AppData = Path.Combine(root, "empty-app"),
        ProgramFiles = Path.Combine(root, "empty-pf"),
        ProgramFilesX86 = Path.Combine(root, "empty-pf86"),
        Registry =
        [
            new WeChatRegistryInstall("HKLM\\Weixin", Path.Combine(pf, "Tencent", "Weixin"), "4.1.2.3"),
        ],
    };
    // Plugin still lives under the real appData; point AppData back so the plugin is visible,
    // but hide Program Files so the dir has to come from the registry hint.
    fromRegistry = new WeChatLocateInput
    {
        AppData = appData,
        ProgramFiles = Path.Combine(root, "empty-pf"),
        ProgramFilesX86 = Path.Combine(root, "empty-pf86"),
        Registry = fromRegistry.Registry,
    };
    WeChatOcrLocation regHit = WeChatOcrLocator.Locate(fromRegistry, probe);
    AssertTrue(regHit.Found && regHit.Kind == WeChatOcrKind.Wx4, "registry install location is searched");
    AssertEqual(versionDir, regHit.WeChatDir, "registry version subdirectory");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
}

Console.WriteLine("=== startup decision ===");
var readyLoc = new WeChatOcrLocation { Found = true, Kind = WeChatOcrKind.Wx4, PluginPath = "p", WeChatDir = "d", LaunchExe = "e", Report = "ok" };
WeChatStartupDecision ready = WeChatStartup.Decide(windowsX64: true, readyLoc, fallbackToLocal: true);
AssertTrue(ready.Ready && !ready.FailProcess, "windows + plugin is ready");
WeChatStartupDecision linux = WeChatStartup.Decide(windowsX64: false, readyLoc, fallbackToLocal: true);
AssertTrue(!linux.Ready && !linux.FailProcess && linux.Message.Contains("Windows x64", StringComparison.Ordinal),
    "non-windows falls back");
WeChatStartupDecision hard = WeChatStartup.Decide(windowsX64: false, readyLoc, fallbackToLocal: false);
AssertTrue(!hard.Ready && hard.FailProcess, "fallback disabled fails the process");
AssertTrue(!WeChatStartup.IsWindowsX64(), "this runner is not Windows x64");
AssertEqual("0", WeChatRegistryReader.Read().Count.ToString(), "registry reader is empty off Windows");

Console.WriteLine("=== pool (mocked IPC) ===");
var overlap = new Overlap();
var backends = new CountingBackend[2];
backends[0] = new CountingBackend("a", overlap);
backends[1] = new CountingBackend("b", overlap);
var pool = new WeChatOcrPool(backends);
Task<string>[] calls = Enumerable.Range(0, 4)
    .Select(i => pool.RecognizeAsync("img-" + i, CancellationToken.None))
    .ToArray();
string[] recognized = await Task.WhenAll(calls);
AssertEqual("4", recognized.Length.ToString(), "pool returns every call");
AssertEqual("2", overlap.GlobalMax.ToString(), "two instances overlap");
AssertEqual("1", backends[0].MaxInFlight.ToString(), "instance A stays single-flight");
AssertEqual("1", backends[1].MaxInFlight.ToString(), "instance B stays single-flight");

Console.WriteLine("=== config mode ===");
string? prevMode = Environment.GetEnvironmentVariable("MINIOCR_OCR_MODE");
string? prevPath = Environment.GetEnvironmentVariable("MINIOCR_WECHAT_OCR_PATH");
string? prevInstances = Environment.GetEnvironmentVariable("MINIOCR_WECHAT_INSTANCES");
string? prevRenderMode = Environment.GetEnvironmentVariable("MINIOCR_RENDER_MODE");
string? prevRenderProcesses = Environment.GetEnvironmentVariable("MINIOCR_RENDER_PROCESSES");
string? prevTextLayer = Environment.GetEnvironmentVariable("MINIOCR_OCR_TEXT_LAYER");
try
{
    Environment.SetEnvironmentVariable("MINIOCR_OCR_MODE", "wechat");
    Environment.SetEnvironmentVariable("MINIOCR_WECHAT_OCR_PATH", null);
    Environment.SetEnvironmentVariable("MINIOCR_WECHAT_INSTANCES", null);
    OcrRuntimeConfig cfg = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig
        {
            Mode = "local",
            WeChatOcrPath = "from-file",
            WeChatInstances = 2,
            AutoScaleFromCpu = true,
        },
    });
    AssertEqual("wechat", cfg.Mode, "env mode overrides file");
    AssertEqual("from-file", cfg.WeChatOcrPath ?? "", "file plugin path kept");
    AssertEqual("2", cfg.WeChatInstances.ToString(), "file instance count");
    AssertEqual("local", cfg.WithMode("local").Mode, "WithMode(local) ignores MINIOCR_OCR_MODE");
    AssertTrue(cfg.WeChatFallbackToLocal, "fallback defaults on");

    Environment.SetEnvironmentVariable("MINIOCR_OCR_MODE", "nope");
    AssertEqual("local", OcrRuntimeConfig.ResolveMode("wechat"), "unknown env mode is local");
    Environment.SetEnvironmentVariable("MINIOCR_OCR_MODE", null);
    AssertEqual("wechat", OcrRuntimeConfig.ResolveMode("WeChat"), "file mode wechat");
    AssertEqual("local", OcrRuntimeConfig.CanonicalMode("paddle"), "unknown canonical mode is local");

    Environment.SetEnvironmentVariable("MINIOCR_RENDER_MODE", null);
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_PROCESSES", null);
    AssertEqual("parallel", cfg.RenderMode, "default render mode is parallel");
    AssertTrue(cfg.RenderProcessCount is >= 1 and <= 4, "auto render processes stay in 1..4");
    AssertEqual("parallel", cfg.With().RenderMode, "With copies render mode");
    AssertEqual(cfg.RenderProcessCount.ToString(), cfg.With().RenderProcessCount.ToString(), "With copies render processes");
    AssertEqual("2", OcrRuntimeConfig.ComputeRenderProcesses(4, 2).ToString(), "4 cores and 2 engines -> 2 render processes");
    AssertEqual("4", OcrRuntimeConfig.ComputeRenderProcesses(16, 8).ToString(), "render process auto cap is 4");
    AssertEqual("1", OcrRuntimeConfig.ComputeRenderProcesses(2, 1).ToString(), "small machine uses 1 render process");

    Environment.SetEnvironmentVariable("MINIOCR_RENDER_MODE", "parallel");
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_PROCESSES", "4");
    OcrRuntimeConfig parallelCfg = OcrRuntimeConfig.FromAppConfig(new AppConfigFile());
    AssertEqual("parallel", parallelCfg.RenderMode, "env render mode overrides file");
    AssertEqual("4", parallelCfg.RenderProcessCount.ToString(), "env render processes");
    AssertEqual("inprocess", parallelCfg.WithRenderMode("inprocess").RenderMode, "WithRenderMode ignores MINIOCR_RENDER_MODE");

    Environment.SetEnvironmentVariable("MINIOCR_RENDER_MODE", "inprocess");
    AssertEqual("inprocess", OcrRuntimeConfig.FromAppConfig(new AppConfigFile()).RenderMode, "env inprocess overrides default");

    Environment.SetEnvironmentVariable("MINIOCR_RENDER_MODE", "nope");
    AssertEqual("parallel", OcrRuntimeConfig.ResolveRenderMode("inprocess"), "unknown env render mode is parallel");
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_MODE", null);
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_PROCESSES", null);
    AssertEqual("inprocess", OcrRuntimeConfig.ResolveRenderMode("InProcess"), "file render mode inprocess");
    OcrRuntimeConfig fileInProcess = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { RenderMode = "inprocess", AutoScaleFromCpu = false },
    });
    AssertEqual("inprocess", fileInProcess.RenderMode, "file can select inprocess");
    OcrRuntimeConfig fileRender = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { RenderMode = "parallel", RenderProcesses = 3, AutoScaleFromCpu = false },
    });
    AssertEqual("parallel", fileRender.RenderMode, "file render mode");
    AssertEqual("3", fileRender.RenderProcessCount.ToString(), "file render processes");
    List<string> fallbackLines = [];
    OcrRuntimeConfig fallen = ParallelStartupFallback.Apply(
        fileRender,
        new InvalidOperationException("spawn denied"),
        fallbackLines.Add);
    AssertEqual("inprocess", fallen.RenderMode, "spawn failure falls back to inprocess");
    AssertEqual("parallel", fallen.WithRenderMode("parallel").RenderMode, "fallback copy can return to parallel");
    AssertTrue(
        fallbackLines.Count == 1 && fallbackLines[0].Contains("Falling back to in-process rendering", StringComparison.Ordinal),
        "spawn failure log line");
    AssertTrue(fallbackLines[0].Contains("spawn denied", StringComparison.Ordinal), "spawn failure log includes the cause");
    int logs = 0;
    AssertEqual("parallel", ParallelStartupFallback.Apply(fileRender, null, _ => logs++).RenderMode, "no spawn failure keeps parallel");
    AssertEqual("0", logs.ToString(), "no spawn failure does not log");
    AssertEqual(
        "inprocess",
        ParallelStartupFallback.Apply(fileInProcess, new InvalidOperationException("ignored"), _ => logs++).RenderMode,
        "inprocess config is unchanged when spawn is not attempted");
    AssertEqual("0", logs.ToString(), "inprocess config does not log a fallback");
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_PROCESSES", "99");
    AssertEqual("8", OcrRuntimeConfig.FromAppConfig(new AppConfigFile()).RenderProcessCount.ToString(), "render processes clamp to 8");
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_PROCESSES", "0");
    AssertEqual("1", OcrRuntimeConfig.FromAppConfig(new AppConfigFile()).RenderProcessCount.ToString(), "render processes clamp to 1");

    Environment.SetEnvironmentVariable("MINIOCR_OCR_TEXT_LAYER", null);
    AssertEqual("auto", OcrRuntimeConfig.FromAppConfig(new AppConfigFile()).TextLayer, "default text layer is auto");
    AssertEqual("off", OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { TextLayer = "off" },
    }).TextLayer, "file text layer off");
    Environment.SetEnvironmentVariable("MINIOCR_OCR_TEXT_LAYER", "FORCE");
    AssertEqual("force", OcrRuntimeConfig.ResolveTextLayer("off"), "env text layer overrides file");
    Environment.SetEnvironmentVariable("MINIOCR_OCR_TEXT_LAYER", "nope");
    AssertEqual("auto", OcrRuntimeConfig.ResolveTextLayer("off"), "unknown text layer is auto");
    Environment.SetEnvironmentVariable("MINIOCR_OCR_TEXT_LAYER", null);
    Environment.SetEnvironmentVariable("MINIOCR_TEXT_LAYER_MIN_CHARS", "80");
    AssertEqual("80", OcrRuntimeConfig.FromAppConfig(new AppConfigFile()).TextLayerMinChars.ToString(), "env min chars");
    Environment.SetEnvironmentVariable("MINIOCR_TEXT_LAYER_MIN_CHARS", null);
}
finally
{
    Environment.SetEnvironmentVariable("MINIOCR_OCR_MODE", prevMode);
    Environment.SetEnvironmentVariable("MINIOCR_WECHAT_OCR_PATH", prevPath);
    Environment.SetEnvironmentVariable("MINIOCR_WECHAT_INSTANCES", prevInstances);
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_MODE", prevRenderMode);
    Environment.SetEnvironmentVariable("MINIOCR_RENDER_PROCESSES", prevRenderProcesses);
    Environment.SetEnvironmentVariable("MINIOCR_OCR_TEXT_LAYER", prevTextLayer);
    Environment.SetEnvironmentVariable("MINIOCR_TEXT_LAYER_MIN_CHARS", null);
}

Console.WriteLine("=== non-ASCII PDF path ===");
AssertEqual(
    @"\\?\C:\Users\mafuz\OneDrive\Desktop\合同.pdf",
    LocalPdfFile.ToExtendedPath(@"C:\Users\mafuz\OneDrive\Desktop\合同.pdf", windows: true),
    "Windows extended path keeps Chinese");
AssertEqual(
    @"\\?\UNC\server\share\合同.pdf",
    LocalPdfFile.ToExtendedPath(@"\\server\share\合同.pdf", windows: true),
    "UNC extended path");
string uniDir = Path.Combine(Path.GetTempPath(), "miniocr-合同-" + Guid.NewGuid().ToString("N"));
string uniPdf = Path.Combine(uniDir, "测试.pdf");
try
{
    Directory.CreateDirectory(uniDir);
    File.WriteAllBytes(uniPdf, "%PDF-1.4\n"u8.ToArray());
    byte[] read = LocalPdfFile.ReadAllBytes(uniPdf);
    AssertTrue(LocalPdfFile.LooksLikePdf(read), "Unicode path round-trip");
    using RentedBuffer rented = LocalPdfFile.ReadAsRented(uniPdf);
    AssertEqual("5", Math.Min(5, rented.Length).ToString(), "rented buffer has the header");
}
finally
{
    try { Directory.Delete(uniDir, recursive: true); } catch { /* ignore */ }
}

Console.WriteLine(failed == 0 ? "\nAll WeChat OCR checks passed." : $"\n{failed} WeChat OCR check(s) failed.");
return failed == 0 ? 0 : 1;

sealed class Overlap
{
    private int _global;
    private int _globalMax;
    private readonly object _gate = new();

    public int GlobalMax
    {
        get { lock (_gate) return _globalMax; }
    }

    public void Enter()
    {
        int now = Interlocked.Increment(ref _global);
        lock (_gate)
        {
            if (now > _globalMax)
                _globalMax = now;
        }
    }

    public void Exit() => Interlocked.Decrement(ref _global);
}

sealed class CountingBackend : IWeChatOcrBackend
{
    private readonly string _name;
    private readonly Overlap _overlap;
    private int _inFlight;
    private int _maxInFlight;
    private readonly object _gate = new();

    public CountingBackend(string name, Overlap overlap)
    {
        _name = name;
        _overlap = overlap;
    }

    public int MaxInFlight
    {
        get { lock (_gate) return _maxInFlight; }
    }

    public async Task<string> RecognizeAsync(string absoluteImagePath, CancellationToken cancellationToken)
    {
        int now = Interlocked.Increment(ref _inFlight);
        lock (_gate)
        {
            if (now > _maxInFlight)
                _maxInFlight = now;
        }

        _overlap.Enter();
        try
        {
            await Task.Delay(50, cancellationToken);
            return _name + ":" + absoluteImagePath;
        }
        finally
        {
            _overlap.Exit();
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
