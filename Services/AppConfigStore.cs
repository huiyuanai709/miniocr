using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MiniOcr;
using System.Text.Json;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Loads / creates MiniOcr config.json with platform-aware path resolution.
///
/// Priority:
/// 1. Env MINIOCR_CONFIG_PATH (explicit file path)
/// 2. First existing file among platform candidates (read in place; do not move)
/// 3. Canonical create location when none exist
///
/// Paths:
/// - Windows: %APPDATA%\MiniOcr\config.json (fallback: %USERPROFILE%\AppData\Roaming\...)
/// - macOS: ~/Library/Application Support/MiniOcr/config.json (primary) AND ~/.config/MiniOcr/config.json (fallback read)
/// - Linux: ~/.config/MiniOcr/config.json (and ApplicationData if different)
///
/// Never treats an empty ApplicationData folder path as valid — falls back to HOME/USERPROFILE.
/// </summary>
public static class AppConfigStore
{
    public const string DirName = "MiniOcr";
    public const string FileName = "config.json";
    public const string ConfigPathEnvVar = "MINIOCR_CONFIG_PATH";

    /// <summary>Result of LoadOrCreate: absolute path + whether the file already existed.</summary>
    public sealed class LoadResult
    {
        public required AppConfigFile Config { get; init; }
        public required string ConfigPath { get; init; }
        public required bool ConfigFileExisted { get; init; }
        public required string PathSource { get; init; }
    }

    /// <summary>Absolute path that will be used (env / existing candidate / canonical).</summary>
    public static string GetConfigPath() => ResolveConfigPath().Path;

    /// <summary>Directory of <see cref="GetConfigPath"/>.</summary>
    public static string GetConfigDirectory() =>
        Path.GetDirectoryName(GetConfigPath()) ?? GetCanonicalConfigDirectory();

    /// <summary>
    /// Resolves which config file path to use. Does not create files.
    /// </summary>
    public static ResolvedConfigPath ResolveConfigPath(
        string? envConfigPath = null,
        string? applicationData = null,
        string? homeDirectory = null,
        OSPlatform? os = null)
    {
        // null = consult process env; non-null (incl. "") = use that value only
        string? envPath = envConfigPath is null
            ? Environment.GetEnvironmentVariable(ConfigPathEnvVar)
            : envConfigPath;
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            string absolute = MakeAbsolute(envPath.Trim());
            return new ResolvedConfigPath(absolute, "env", File.Exists(absolute));
        }

        OSPlatform platform = os ?? CurrentOsPlatform();
        // null = read from process; "" = explicitly empty (tests / forced fallback)
        string? appData = applicationData is null
            ? NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            : NullIfEmpty(applicationData);
        string? home = homeDirectory is null
            ? ResolveHomeDirectory()
            : NullIfEmpty(homeDirectory);

        foreach (string candidate in EnumerateCandidatePaths(platform, appData, home))
        {
            if (File.Exists(candidate))
            {
                return new ResolvedConfigPath(candidate, "existing", Existed: true);
            }
        }

        // Use private overload with already-resolved appData/home (null = empty, do NOT re-read process).
        string canonical = MakeAbsolute(Path.Combine(GetCanonicalConfigDirectory(platform, appData, home), FileName));
        return new ResolvedConfigPath(canonical, "canonical", File.Exists(canonical));
    }

    /// <summary>
    /// Ensures directory + sample config exist at the resolved path, then deserializes.
    /// Never logs secrets.
    /// </summary>
    public static LoadResult LoadOrCreate(ILogger? logger = null)
    {
        ResolvedConfigPath resolved = ResolveConfigPath();
        string path = resolved.Path;
        bool existed = resolved.Existed;
        string dir = Path.GetDirectoryName(path) ?? ".";

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            logger?.LogInformation("Created config directory: {Dir}", Path.GetFullPath(dir));
        }

        if (!File.Exists(path))
        {
            string sample = BuildSampleJson();
            File.WriteAllText(path, sample);
            logger?.LogInformation("Wrote sample config: {Path}", Path.GetFullPath(path));
        }

        string absolutePath = Path.GetFullPath(path);
        AppConfigFile config;
        try
        {
            string json = File.ReadAllText(path);
            AppConfigFile? file = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfigFile);
            config = file ?? new AppConfigFile();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse config at {Path}; using defaults", absolutePath);
            config = new AppConfigFile();
        }

        logger?.LogInformation(
            "Config path={Path} existed={Existed} source={Source}",
            absolutePath,
            existed,
            resolved.Source);

        return new LoadResult
        {
            Config = config,
            ConfigPath = absolutePath,
            ConfigFileExisted = existed,
            PathSource = resolved.Source,
        };
    }

    public static LlmRuntimeConfig ResolveLlm(AppConfigFile file)
    {
        LlmFileConfig llm = file.Llm ?? new LlmFileConfig();

        string baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_LLM_BASE_URL"),
            llm.BaseUrl) ?? "https://api.openai.com";

        string apiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_LLM_API_KEY"),
            llm.ApiKey) ?? "";

        string model = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_LLM_MODEL"),
            llm.Model) ?? "gpt-4o-mini";

        int maxConcurrency = llm.MaxConcurrency <= 0 ? 8 : llm.MaxConcurrency;
        string? envConcurrency = Environment.GetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY");
        if (!string.IsNullOrWhiteSpace(envConcurrency) &&
            int.TryParse(envConcurrency.Trim(), out int parsedConcurrency))
        {
            maxConcurrency = parsedConcurrency;
        }

        int ocrConcurrency = llm.OcrConcurrency <= 0 ? 32 : llm.OcrConcurrency;
        string? envOcrConcurrency = Environment.GetEnvironmentVariable("MINIOCR_LLM_OCR_CONCURRENCY");
        if (!string.IsNullOrWhiteSpace(envOcrConcurrency) &&
            int.TryParse(envOcrConcurrency.Trim(), out int parsedOcrConcurrency))
        {
            ocrConcurrency = parsedOcrConcurrency;
        }

        int ocrMaxChars = llm.OcrMaxCharsHint <= 0 ? 8000 : llm.OcrMaxCharsHint;

        int ocrJpegQuality = llm.OcrJpegQuality <= 0 ? 70 : llm.OcrJpegQuality;
        string? envJpegQ = Environment.GetEnvironmentVariable("MINIOCR_LLM_OCR_JPEG_QUALITY");
        if (!string.IsNullOrWhiteSpace(envJpegQ) &&
            int.TryParse(envJpegQ.Trim(), out int parsedJpegQ))
        {
            ocrJpegQuality = parsedJpegQ;
        }

        bool thinking = llm.Thinking;
        string? envThinking = Environment.GetEnvironmentVariable("MINIOCR_LLM_THINKING");
        if (!string.IsNullOrWhiteSpace(envThinking))
            thinking = ThinkingConfigJsonConverter.ParseThinkingString(envThinking);

        return new LlmRuntimeConfig
        {
            Enabled = llm.Enabled,
            BaseUrl = baseUrl.TrimEnd('/'),
            ApiKey = apiKey,
            Model = model,
            TimeoutSeconds = Math.Clamp(llm.TimeoutSeconds <= 0 ? 120 : llm.TimeoutSeconds, 5, 600),
            MaxCharsPerRequest = Math.Clamp(
                llm.MaxCharsPerRequest <= 0 ? 300_000 : llm.MaxCharsPerRequest, 1000, 2_000_000),
            MaxConcurrency = Math.Clamp(maxConcurrency, 1, 32),
            OcrConcurrency = Math.Clamp(ocrConcurrency, 1, 256),
            OcrMaxCharsHint = Math.Clamp(ocrMaxChars, 500, 100_000),
            OcrJpegQuality = Math.Clamp(ocrJpegQuality, 40, 95),
            Thinking = thinking,
            FallbackToHeuristics = llm.FallbackToHeuristics,
        };
    }

    /// <summary>Merge <c>hunyuan</c> file section with <c>MINIOCR_HUNYUAN_*</c> overrides. Never logs the api key.</summary>
    public static HunyuanRuntimeConfig ResolveHunyuan(AppConfigFile file)
    {
        HunyuanFileConfig hy = file.Hunyuan ?? new HunyuanFileConfig();

        string baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_BASE_URL"),
            hy.BaseUrl) ?? "http://127.0.0.1:8000";

        string apiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_API_KEY"),
            hy.ApiKey) ?? "";

        string model = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_MODEL"),
            hy.Model) ?? "tencent/HunyuanOCR";

        int concurrency = hy.Concurrency <= 0 ? 2 : hy.Concurrency;
        if (int.TryParse(Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_CONCURRENCY"), out int parsedConcurrency))
            concurrency = parsedConcurrency;

        int jpegQuality = hy.JpegQuality <= 0 ? 85 : hy.JpegQuality;
        if (int.TryParse(Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_JPEG_QUALITY"), out int parsedJpeg))
            jpegQuality = parsedJpeg;

        int maxTokens = hy.MaxTokens <= 0 ? 8000 : hy.MaxTokens;
        if (int.TryParse(Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_MAX_TOKENS"), out int parsedMaxTokens))
            maxTokens = parsedMaxTokens;

        int timeout = hy.TimeoutSeconds <= 0 ? 180 : hy.TimeoutSeconds;
        if (int.TryParse(Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_TIMEOUT"), out int parsedTimeout))
            timeout = parsedTimeout;

        bool enabled = hy.Enabled;
        string? envEnabled = Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_ENABLED");
        if (!string.IsNullOrWhiteSpace(envEnabled))
            enabled = ThinkingConfigJsonConverter.ParseThinkingString(envEnabled);

        string prompt = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_HUNYUAN_PROMPT"),
            hy.Prompt) ?? HunyuanRuntimeConfig.DefaultDocumentPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = HunyuanRuntimeConfig.DefaultDocumentPrompt;

        return new HunyuanRuntimeConfig
        {
            Enabled = enabled,
            BaseUrl = baseUrl.TrimEnd('/'),
            ApiKey = apiKey,
            Model = model.Trim(),
            TimeoutSeconds = Math.Clamp(timeout, 10, 600),
            Concurrency = Math.Clamp(concurrency, 1, 64),
            JpegQuality = Math.Clamp(jpegQuality, 40, 95),
            MaxTokens = Math.Clamp(maxTokens, 256, 16384),
            Prompt = prompt.Trim(),
        };
    }

    /// <summary>Candidate paths in preference order (may include duplicates; caller should de-dupe).</summary>
    public static IEnumerable<string> EnumerateCandidatePaths(
        OSPlatform platform,
        string? applicationData,
        string? homeDirectory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in EnumerateCandidatePathsCore(platform, applicationData, homeDirectory))
        {
            string absolute = MakeAbsolute(raw);
            if (seen.Add(absolute))
                yield return absolute;
        }
    }

    public static string GetCanonicalConfigPath(
        OSPlatform? os = null,
        string? applicationData = null,
        string? homeDirectory = null)
    {
        OSPlatform platform = os ?? CurrentOsPlatform();
        string? appData = applicationData is null
            ? NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            : NullIfEmpty(applicationData);
        string? home = homeDirectory is null
            ? ResolveHomeDirectory()
            : NullIfEmpty(homeDirectory);
        return MakeAbsolute(Path.Combine(GetCanonicalConfigDirectory(platform, appData, home), FileName));
    }

    public static string GetCanonicalConfigDirectory(
        OSPlatform? os = null,
        string? applicationData = null,
        string? homeDirectory = null)
    {
        OSPlatform platform = os ?? CurrentOsPlatform();
        string? appData = applicationData is null
            ? NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            : NullIfEmpty(applicationData);
        string? home = homeDirectory is null
            ? ResolveHomeDirectory()
            : NullIfEmpty(homeDirectory);
        return GetCanonicalConfigDirectory(platform, appData, home);
    }

    public readonly record struct ResolvedConfigPath(string Path, string Source, bool Existed);

    private static IEnumerable<string> EnumerateCandidatePathsCore(
        OSPlatform platform,
        string? applicationData,
        string? homeDirectory)
    {
        if (platform == OSPlatform.OSX)
        {
            // Primary: Application Support (real macOS ApplicationData)
            if (!string.IsNullOrWhiteSpace(applicationData))
                yield return Path.Combine(applicationData, DirName, FileName);
            else if (!string.IsNullOrWhiteSpace(homeDirectory))
                yield return Path.Combine(homeDirectory, "Library", "Application Support", DirName, FileName);

            // Fallback: XDG-style ~/.config (users may have put the file here due to old docs)
            if (!string.IsNullOrWhiteSpace(homeDirectory))
                yield return Path.Combine(homeDirectory, ".config", DirName, FileName);
            yield break;
        }

        if (platform == OSPlatform.Windows)
        {
            if (!string.IsNullOrWhiteSpace(applicationData))
                yield return Path.Combine(applicationData, DirName, FileName);
            else if (!string.IsNullOrWhiteSpace(homeDirectory))
                yield return Path.Combine(homeDirectory, "AppData", "Roaming", DirName, FileName);
            yield break;
        }

        // Linux and other Unix: prefer ~/.config, then ApplicationData if different
        if (!string.IsNullOrWhiteSpace(homeDirectory))
            yield return Path.Combine(homeDirectory, ".config", DirName, FileName);

        if (!string.IsNullOrWhiteSpace(applicationData))
            yield return Path.Combine(applicationData, DirName, FileName);
    }

    private static string GetCanonicalConfigDirectory(
        OSPlatform platform,
        string? applicationData,
        string? homeDirectory)
    {
        if (platform == OSPlatform.Linux)
        {
            if (!string.IsNullOrWhiteSpace(homeDirectory))
                return Path.Combine(homeDirectory, ".config", DirName);
            if (!string.IsNullOrWhiteSpace(applicationData))
                return Path.Combine(applicationData, DirName);
            return Path.Combine(".", DirName);
        }

        // Windows + macOS: ApplicationData/MiniOcr, with HOME/USERPROFILE fallback
        if (!string.IsNullOrWhiteSpace(applicationData))
            return Path.Combine(applicationData, DirName);

        if (platform == OSPlatform.OSX && !string.IsNullOrWhiteSpace(homeDirectory))
            return Path.Combine(homeDirectory, "Library", "Application Support", DirName);

        if (platform == OSPlatform.Windows && !string.IsNullOrWhiteSpace(homeDirectory))
            return Path.Combine(homeDirectory, "AppData", "Roaming", DirName);

        if (!string.IsNullOrWhiteSpace(homeDirectory))
            return Path.Combine(homeDirectory, ".config", DirName);

        return Path.Combine(".", DirName);
    }

    public static string? ResolveHomeDirectory()
    {
        string? home = NullIfEmpty(Environment.GetEnvironmentVariable("HOME"));
        if (home is not null)
            return home;
        return NullIfEmpty(Environment.GetEnvironmentVariable("USERPROFILE"));
    }

    private static OSPlatform CurrentOsPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return OSPlatform.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OSPlatform.OSX;
        return OSPlatform.Linux;
    }

    private static string MakeAbsolute(string path)
    {
        // Keep Windows drive-letter paths intact when not running on Windows
        // (smoke tests may resolve OSPlatform.Windows on Linux).
        if (path.Length >= 3 &&
            char.IsLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/') &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return path.Replace('/', '\\');
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a))
            return a.Trim();
        if (!string.IsNullOrWhiteSpace(b))
            return b.Trim();
        return null;
    }

    private static string BuildSampleJson() =>
        """
        {
          "llm": {
            "enabled": true,
            "baseUrl": "https://api.openai.com",
            "apiKey": "",
            "model": "gpt-4o-mini",
            "timeoutSeconds": 120,
            "maxCharsPerRequest": 300000,
            "maxConcurrency": 8,
            "ocrConcurrency": 32,
            "ocrMaxCharsHint": 8000,
            "ocrJpegQuality": 70,
            "thinking": false,
            "fallbackToHeuristics": false
          },
          "ocr": {
            "mode": "local",
            "dpi": 96,
            "engines": null,
            "lineWorkers": null,
            "detThreads": null,
            "rasterWorkers": null,
            "useCls": false,
            "autoScaleFromCpu": true
          },
          "hunyuan": {
            "enabled": true,
            "baseUrl": "http://127.0.0.1:8000",
            "apiKey": "",
            "model": "tencent/HunyuanOCR",
            "timeoutSeconds": 180,
            "concurrency": 2,
            "jpegQuality": 85,
            "maxTokens": 8000,
            "prompt": ""
          }
        }
        """;
}
