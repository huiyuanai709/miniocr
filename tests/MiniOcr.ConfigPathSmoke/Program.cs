using System.Runtime.InteropServices;
using System.Text.Json;
using MiniOcr;
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

Console.WriteLine("=== AppConfigStore path resolution smoke ===");

string home = "/Users/demo";
string appSupport = "/Users/demo/Library/Application Support";
string xdg = "/Users/demo/.config/MiniOcr/config.json";
string macPrimary = "/Users/demo/Library/Application Support/MiniOcr/config.json";

// macOS: empty ApplicationData → HOME-based Application Support (not relative MiniOcr/...)
{
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: "",
        homeDirectory: home,
        os: OSPlatform.OSX);
    AssertEqual(macPrimary, resolved.Path, "mac empty AppData → Application Support canonical");
    AssertEqual("canonical", resolved.Source, "mac empty AppData source=canonical");
    AssertTrue(!resolved.Path.StartsWith("MiniOcr", StringComparison.Ordinal), "mac path must not be relative MiniOcr/...");
}

// macOS: candidates include Application Support then ~/.config
{
    var candidates = AppConfigStore.EnumerateCandidatePaths(OSPlatform.OSX, appSupport, home).ToList();
    AssertTrue(candidates.Count >= 2, "mac has ≥2 candidates");
    AssertEqual(macPrimary, candidates[0], "mac candidate[0]=Application Support");
    AssertEqual(xdg, candidates[1], "mac candidate[1]=~/.config");
}

// macOS: existing ~/.config wins over missing Application Support (read-without-move)
{
    string tmpRoot = Path.Combine(Path.GetTempPath(), "miniocr-config-smoke-" + Guid.NewGuid().ToString("N"));
    string fakeHome = Path.Combine(tmpRoot, "home");
    string fakeAppSupport = Path.Combine(fakeHome, "Library", "Application Support");
    string xdgDir = Path.Combine(fakeHome, ".config", "MiniOcr");
    Directory.CreateDirectory(xdgDir);
    string xdgFile = Path.Combine(xdgDir, "config.json");
    File.WriteAllText(xdgFile, """{"llm":{"enabled":true,"apiKey":"from-xdg"}}""");

    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: fakeAppSupport,
        homeDirectory: fakeHome,
        os: OSPlatform.OSX);

    AssertEqual(Path.GetFullPath(xdgFile), resolved.Path, "mac existing ~/.config is used");
    AssertEqual("existing", resolved.Source, "mac ~/.config source=existing");
    AssertTrue(resolved.Existed, "mac ~/.config existed=true");

    // LoadOrCreate should read in place (not move to Application Support)
    string? prev = Environment.GetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar);
    try
    {
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, null);
        // Resolve via explicit env to avoid touching real AppData
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, xdgFile);
        var loaded = AppConfigStore.LoadOrCreate();
        AssertEqual(Path.GetFullPath(xdgFile), loaded.ConfigPath, "LoadOrCreate keeps xdg path");
        AssertTrue(loaded.ConfigFileExisted, "LoadOrCreate existed=true for xdg");
        AssertTrue(loaded.Config.Llm?.ApiKey == "from-xdg", "LoadOrCreate reads xdg apiKey");
        AssertTrue(!Directory.Exists(Path.Combine(fakeAppSupport, "MiniOcr")), "did not create Application Support copy");
    }
    finally
    {
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, prev);
        try { Directory.Delete(tmpRoot, recursive: true); } catch { /* ignore */ }
    }
}

// Env override wins
{
    string custom = Path.Combine(Path.GetTempPath(), "miniocr-custom-config.json");
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: custom,
        applicationData: appSupport,
        homeDirectory: home,
        os: OSPlatform.OSX);
    AssertEqual(Path.GetFullPath(custom), resolved.Path, "MINIOCR_CONFIG_PATH wins");
    AssertEqual("env", resolved.Source, "env source");
}

// Linux canonical is ~/.config
{
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: "",
        homeDirectory: "/home/demo",
        os: OSPlatform.Linux);
    AssertEqual("/home/demo/.config/MiniOcr/config.json", resolved.Path, "linux canonical ~/.config");
}

// Windows: empty AppData → USERPROFILE Roaming
{
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: "",
        homeDirectory: @"C:\Users\demo",
        os: OSPlatform.Windows);
    string expected = @"C:\Users\demo\AppData\Roaming\MiniOcr\config.json";
    AssertEqual(expected, resolved.Path, "win empty AppData → Roaming fallback");
}

Console.WriteLine("=== OCR mode + Hunyuan config ===");

string[] modeEnvKeys =
[
    "MINIOCR_OCR_MODE",
    "MINIOCR_DPI",
    "MINIOCR_RASTER_WORKERS",
    "MINIOCR_HUNYUAN_BASE_URL",
    "MINIOCR_HUNYUAN_API_KEY",
    "MINIOCR_HUNYUAN_MODEL",
    "MINIOCR_HUNYUAN_CONCURRENCY",
    "MINIOCR_HUNYUAN_JPEG_QUALITY",
    "MINIOCR_HUNYUAN_MAX_TOKENS",
    "MINIOCR_HUNYUAN_PROMPT",
    "MINIOCR_HUNYUAN_TIMEOUT",
    "MINIOCR_HUNYUAN_ENABLED",
];
Dictionary<string, string?> savedModeEnv = modeEnvKeys.ToDictionary(
    k => k,
    k => Environment.GetEnvironmentVariable(k));
try
{
    foreach (string key in modeEnvKeys)
        Environment.SetEnvironmentVariable(key, null);

    OcrRuntimeConfig local = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { Mode = "local" },
    });
    AssertEqual("local", local.Mode, "file mode local");
    AssertTrue(local.DefaultDpi == 96, "local default dpi 96");

    OcrRuntimeConfig llm = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { Mode = "llm" },
    });
    AssertEqual("llm", llm.Mode, "file mode llm");
    AssertTrue(llm.DefaultDpi == 72, "llm default dpi 72");
    AssertTrue(llm.IsLlmMode && !llm.IsHunyuanMode, "llm flags");

    OcrRuntimeConfig hunyuan = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { Mode = "Hunyuan" },
    });
    AssertEqual("hunyuan", hunyuan.Mode, "file mode Hunyuan normalized");
    AssertTrue(hunyuan.DefaultDpi == 144, "hunyuan default dpi 144");
    AssertTrue(hunyuan.IsHunyuanMode && !hunyuan.IsLlmMode, "hunyuan flags");
    AssertTrue(hunyuan.RasterWorkerCount == llm.RasterWorkerCount, "hunyuan raster workers match llm remote default");

    OcrRuntimeConfig explicitDpi = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { Mode = "hunyuan", Dpi = 96 },
    });
    AssertTrue(explicitDpi.DefaultDpi == 96, "explicit ocr.dpi wins over hunyuan default");

    OcrRuntimeConfig unknown = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { Mode = "onnx" },
    });
    AssertEqual("local", unknown.Mode, "unknown mode falls back to local");

    Environment.SetEnvironmentVariable("MINIOCR_OCR_MODE", "hunyuan");
    OcrRuntimeConfig envMode = OcrRuntimeConfig.FromAppConfig(new AppConfigFile
    {
        Ocr = new OcrFileConfig { Mode = "local" },
    });
    AssertEqual("hunyuan", envMode.Mode, "MINIOCR_OCR_MODE overrides file");
    OcrRuntimeConfig forced = envMode.ForceMode("local");
    AssertEqual("local", forced.Mode, "ForceMode(local) ignores MINIOCR_OCR_MODE");
    AssertEqual("hunyuan", envMode.WithMode("local").Mode, "WithMode re-reads MINIOCR_OCR_MODE");
    Environment.SetEnvironmentVariable("MINIOCR_OCR_MODE", null);

    HunyuanRuntimeConfig hyDefault = AppConfigStore.ResolveHunyuan(new AppConfigFile());
    AssertTrue(hyDefault.IsUsable, "default hunyuan client is usable");
    AssertEqual("http://127.0.0.1:8000", hyDefault.BaseUrl, "default hunyuan baseUrl");
    AssertEqual("tencent/HunyuanOCR", hyDefault.Model, "default hunyuan model");
    AssertEqual(HunyuanRuntimeConfig.DefaultDocumentPrompt, hyDefault.Prompt, "empty prompt uses official document prompt");
    AssertTrue(hyDefault.Concurrency == 2, "default hunyuan concurrency 2");
    AssertTrue(hyDefault.MaxTokens == 8000, "default maxTokens 8000");
    AssertTrue(hyDefault.ApiKey.Length == 0, "default hunyuan apiKey empty");

    Environment.SetEnvironmentVariable("MINIOCR_HUNYUAN_BASE_URL", "http://127.0.0.1:8080/");
    Environment.SetEnvironmentVariable("MINIOCR_HUNYUAN_MODEL", "HYVL");
    Environment.SetEnvironmentVariable("MINIOCR_HUNYUAN_CONCURRENCY", "1");
    Environment.SetEnvironmentVariable("MINIOCR_HUNYUAN_PROMPT", HunyuanRuntimeConfig.PlainTextPrompt);
    Environment.SetEnvironmentVariable("MINIOCR_HUNYUAN_ENABLED", "true");
    HunyuanRuntimeConfig hyEnv = AppConfigStore.ResolveHunyuan(new AppConfigFile
    {
        Hunyuan = new HunyuanFileConfig { Enabled = false, Model = "ignored", BaseUrl = "http://example.invalid" },
    });
    AssertEqual("http://127.0.0.1:8080", hyEnv.BaseUrl, "hunyuan baseUrl env strips slash");
    AssertEqual("HYVL", hyEnv.Model, "hunyuan model env");
    AssertTrue(hyEnv.Concurrency == 1, "hunyuan concurrency env");
    AssertEqual(HunyuanRuntimeConfig.PlainTextPrompt, hyEnv.Prompt, "hunyuan prompt env");
    AssertTrue(hyEnv.Enabled, "hunyuan enabled env overrides file false");

    string wire = JsonSerializer.Serialize(
        new HunyuanChatCompletionRequest
        {
            Model = "tencent/HunyuanOCR",
            Temperature = 0,
            MaxTokens = 8000,
            Messages =
            [
                new HunyuanChatMessage
                {
                    Role = "user",
                    Content =
                    [
                        new HunyuanContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new HunyuanImageUrl { Url = "data:image/jpeg;base64,QQ" },
                        },
                        new HunyuanContentPart { Type = "text", Text = HunyuanRuntimeConfig.DefaultDocumentPrompt },
                    ],
                },
            ],
        },
        AppJsonContext.Default.HunyuanChatCompletionRequest);
    AssertTrue(wire.Contains("\"image_url\"", StringComparison.Ordinal), "wire uses image_url");
    AssertTrue(wire.Contains("\"max_tokens\"", StringComparison.Ordinal), "wire uses max_tokens");
    AssertTrue(!wire.Contains("imageUrl", StringComparison.Ordinal), "wire does not camelCase imageUrl");
    AssertTrue(!wire.Contains("maxTokens", StringComparison.Ordinal), "wire does not camelCase maxTokens");
    int imageAt = wire.IndexOf("\"type\":\"image_url\"", StringComparison.Ordinal);
    int textAt = wire.IndexOf("\"type\":\"text\"", StringComparison.Ordinal);
    AssertTrue(imageAt >= 0 && textAt > imageAt, "image content part precedes the text part");
    AssertEqual(
        "http://127.0.0.1:8080/v1/chat/completions",
        HunyuanRuntimeConfig.ChatCompletionsUrl("http://127.0.0.1:8080/v1"),
        "trailing /v1 is not doubled");
}
finally
{
    foreach (KeyValuePair<string, string?> kv in savedModeEnv)
        Environment.SetEnvironmentVariable(kv.Key, kv.Value);
}

Console.WriteLine(failed == 0 ? "\nAll config-path checks passed." : $"\n{failed} config-path check(s) failed.");
return failed == 0 ? 0 : 1;
