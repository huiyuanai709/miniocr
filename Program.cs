using System.Runtime.InteropServices;
using MiniOcr;
using Sdcb.SimdPaddleOCR;
using PDFtoImage.Parallel;
using MiniOcr.Models;
using MiniOcr.Services;

// PDFtoImage.Parallel re-launches this executable as a render worker and sets
// PDFTOIMAGE_PARALLEL_WORKER_PIPE. Native AOT enters that path from a module
// initializer (CoreCLR from a startup hook) and exits before Main. If the
// bootstrap was trimmed away, the variable is still set here — refuse to start
// the web host or load OCR models in the child.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PDFTOIMAGE_PARALLEL_WORKER_PIPE")))
{
    Console.Error.WriteLine(
        "PDFtoImage.Parallel worker bootstrap did not run; refusing to start the MiniOcr host.");
    Environment.Exit(1);
}

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
// CreateSlimBuilder ships without the template's appsettings.json, so every
// cluster HTTP call is Information: hosting "Request starting/finished" plus
// "Executing endpoint" on the coordinator, and four HttpClient lines per call
// on the worker. Idle dispatch polls (4 Hz) drown OCR progress. Match the
// ASP.NET template for Microsoft.AspNetCore, and keep HttpClient at Warning,
// unless Logging:LogLevel already sets that category.
QuietFrameworkRequestLogs(builder, "Microsoft.AspNetCore", LogLevel.Warning);
QuietFrameworkRequestLogs(builder, "System.Net.Http.HttpClient", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 300L * 1024 * 1024;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 300L * 1024 * 1024;
    options.ValueLengthLimit = 32 * 1024;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

builder.Services.AddHttpClient<ParallelPdfDownloader>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+https://github.com/huiyuanai709/miniocr)");
    client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
});

builder.Services.AddHttpClient(ChallengeJobService.CallbackHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+challenge-callback)");
});

using ILoggerFactory bootstrapLogs = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});
ILogger bootstrapLogger = bootstrapLogs.CreateLogger("MiniOcr.Startup");

AppConfigStore.LoadResult configLoad = AppConfigStore.LoadOrCreate(bootstrapLogger);
AppConfigFile appConfig = configLoad.Config;
string configPath = configLoad.ConfigPath;
bool configFileExisted = configLoad.ConfigFileExisted;
LlmRuntimeConfig llmConfig = AppConfigStore.ResolveLlm(appConfig);
OcrRuntimeConfig runtimeConfig = OcrRuntimeConfig.FromAppConfig(appConfig);
ClusterRuntimeConfig clusterConfig = ClusterRuntimeConfig.Resolve(appConfig);

if (OcrCompareRunner.IsRequested(args))
{
    Environment.ExitCode = await OcrCompareRunner.RunAsync(args, appConfig, bootstrapLogs);
    return;
}

// ocr.mode=llm needs a usable LLM; otherwise fall back to local with a clear warning.
if (runtimeConfig.IsLlmMode && !llmConfig.IsUsable)
{
    bootstrapLogger.LogWarning(
        "ocr.mode=llm but LLM is not usable (need llm.enabled + apiKey + baseUrl + model); falling back to local Paddle OCR");
    runtimeConfig = runtimeConfig.WithMode("local");
}

WeChatOcrEngine? wechatEngine = null;
string wechatStatus = "off";
if (runtimeConfig.IsWeChatMode)
{
    WeChatOcrLocation location = WeChatOcrLocator.Locate(
        WeChatLocateInput.FromConfig(runtimeConfig),
        FileSystemWeChatProbe.Instance);
    Console.WriteLine(location.Report);
    WeChatStartupDecision decision = WeChatStartup.Decide(
        WeChatStartup.IsWindowsX64(),
        location,
        runtimeConfig.WeChatFallbackToLocal);
    if (!decision.Ready)
    {
        string banner = decision.Message +
            (decision.FailProcess
                ? "\nRefusing to start. Set ocr.wechatFallbackToLocal=true or MINIOCR_WECHAT_FALLBACK=1 to use local Paddle instead."
                : "\nFalling back to local Paddle OCR.");
        Console.Error.WriteLine(banner);
        bootstrapLogger.LogWarning("{Message}", banner);
        if (decision.FailProcess)
        {
            Environment.ExitCode = 1;
            return;
        }

        wechatStatus = "fallback: " + FirstLine(decision.Message);
        runtimeConfig = runtimeConfig.WithMode("local");
    }
    else
    {
        try
        {
            wechatEngine = await WeChatOcrEngine.ConnectAsync(
                location,
                runtimeConfig,
                bootstrapLogs.CreateLogger<WeChatOcrEngine>(),
                CancellationToken.None);
            wechatStatus = "ready";
        }
        catch (Exception ex)
        {
            string banner =
                "ocr.mode=wechat failed to connect: " + ex.Message +
                (runtimeConfig.WeChatFallbackToLocal
                    ? "\nFalling back to local Paddle OCR."
                    : "\nRefusing to start. Set ocr.wechatFallbackToLocal=true or MINIOCR_WECHAT_FALLBACK=1 to use local Paddle instead.");
            Console.Error.WriteLine(banner);
            bootstrapLogger.LogWarning(ex, "WeChat OCR connect failed");
            if (!runtimeConfig.WeChatFallbackToLocal)
            {
                Environment.ExitCode = 1;
                return;
            }

            wechatStatus = "fallback: " + FirstLine(ex.Message);
            runtimeConfig = runtimeConfig.WithMode("local");
        }
    }
}

ParallelPdfProcessor? parallelRenderer = null;
if (runtimeConfig.IsParallelRender)
{
    (runtimeConfig, parallelRenderer) = await PdfParallelStartup.ProbeAsync(runtimeConfig, bootstrapLogger);
}

string apiKeyStatus = string.IsNullOrEmpty(llmConfig.ApiKey) ? "(empty)" : "(set)";
bool llmOcrMode = runtimeConfig.IsLlmMode;

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
Console.WriteLine(
    $"Config: path={configPath} existed={configFileExisted} source={configLoad.PathSource}");
Console.WriteLine(
    $"CPU auto-scale: ProcessorCount={runtimeConfig.ProcessorCount}, autoScaleFromCpu={runtimeConfig.AutoScaleFromCpu}");
Console.WriteLine(
    $"OCR mode={runtimeConfig.Mode}, knobs: engines={runtimeConfig.EngineCount}, dpi={runtimeConfig.DefaultDpi}, " +
    $"backend={runtimeConfig.Backend}, lineWorkers={runtimeConfig.LineWorkerCount}, detThreads={runtimeConfig.DetIntraOpThreads}, " +
    $"recIntraOpThreads={runtimeConfig.RecIntraOpThreads}, recBatch={runtimeConfig.RecBatchLines}, " +
    $"useCls={runtimeConfig.UseDirectionClassification}, rasterWorkers={runtimeConfig.RasterWorkerCount}, " +
    $"renderMode={runtimeConfig.RenderMode}, renderProcesses={runtimeConfig.RenderProcessCount}, " +
    $"textLayer={runtimeConfig.TextLayer}, " +
    $"wechatInstances={runtimeConfig.WeChatInstances}, wechatStatus={wechatStatus}");
Console.WriteLine(
    $"LLM: enabled={llmConfig.Enabled}, usable={llmConfig.IsUsable}, " +
    $"model={llmConfig.Model}, baseUrl={llmConfig.BaseUrl}, " +
    $"maxConcurrency={llmConfig.MaxConcurrency}, pagesPerRequest={llmConfig.PagesPerRequest}, " +
    $"maxCharsPerRequest={llmConfig.MaxCharsPerRequest}, " +
    $"ocrConcurrency={llmConfig.OcrConcurrency}, ocrJpegQuality={llmConfig.OcrJpegQuality}, " +
    $"thinking={llmConfig.Thinking}, fallbackToHeuristics={llmConfig.FallbackToHeuristics}, apiKey={apiKeyStatus}");

OcrEngine? engine = null;
if (llmOcrMode)
{
    Console.WriteLine("Skipping ChineseV6Tiny / PaddleOcrAll — ocr.mode=llm (vision OCR).");
}
else if (runtimeConfig.IsWeChatMode)
{
    Console.WriteLine(
        $"Skipping ChineseV6Tiny / PaddleOcrAll — ocr.mode=wechat ({wechatEngine?.KindName}, instances={wechatEngine?.InstanceCount ?? 0}).");
}
else
{
    Console.WriteLine("Loading ChineseV6Tiny OCR models...");
    engine = await OcrEngine.CreateAsync(
        bootstrapLogs.CreateLogger<OcrEngine>(),
        runtimeConfig);
    if (engine.EngineCount != runtimeConfig.EngineCount)
        runtimeConfig = runtimeConfig.With(engineCount: engine.EngineCount);
    Console.WriteLine($"OCR backend requested={runtimeConfig.Backend} effective={engine.EffectiveBackend}");
    if (engine.EffectiveBackend == "metal" && !string.IsNullOrEmpty(engine.GpuDeviceName))
    {
        Console.WriteLine(
            $"Metal device: {engine.GpuDeviceName} recommendedWorkingSetMB={engine.GpuMemoryBytes / (1024 * 1024)} bufferCapMB={engine.GpuBufferCapBytes / (1024 * 1024)} engines={engine.EngineCount}");
    }
    else if (!string.IsNullOrEmpty(engine.VulkanDeviceName))
    {
        Console.WriteLine(
            $"Vulkan device: {engine.VulkanDeviceName} deviceLocalMB={engine.VulkanDeviceLocalBytes / (1024 * 1024)} bufferCapMB={engine.VulkanBufferCapBytes / (1024 * 1024)} engines={engine.EngineCount} fence={OcrVulkan.FenceWait} initMs={OcrVulkan.InitMs:F0}");
        if (!string.IsNullOrEmpty(OcrVulkan.PipelineCachePath))
        {
            long cacheBytes = 0;
            try
            {
                if (File.Exists(OcrVulkan.PipelineCachePath))
                    cacheBytes = new FileInfo(OcrVulkan.PipelineCachePath).Length;
            }
            catch (IOException) { }
            Console.WriteLine(
                $"Vulkan pipeline cache: {OcrVulkan.PipelineCachePath} restored={OcrVulkan.PipelineCacheRestored} bytes={cacheBytes}");
        }
    }
}

builder.Services.AddSingleton(runtimeConfig);
builder.Services.AddSingleton(llmConfig);
if (engine is not null)
    builder.Services.AddSingleton(engine);
if (wechatEngine is not null)
    builder.Services.AddSingleton(wechatEngine);

builder.Services.AddHttpClient(nameof(LlmEntityExtractor), (sp, client) =>
{
    LlmRuntimeConfig cfg = sp.GetRequiredService<LlmRuntimeConfig>();
    client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+LLM-NER)");
});
builder.Services.AddSingleton<LlmEntityExtractor>(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    HttpClient http = factory.CreateClient(nameof(LlmEntityExtractor));
    return new LlmEntityExtractor(
        http,
        sp.GetRequiredService<LlmRuntimeConfig>(),
        sp.GetRequiredService<ILogger<LlmEntityExtractor>>());
});

builder.Services.AddHttpClient(nameof(LlmVisionOcr), (sp, client) =>
{
    LlmRuntimeConfig cfg = sp.GetRequiredService<LlmRuntimeConfig>();
    client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+LLM-vision-OCR)");
});
builder.Services.AddSingleton<LlmVisionOcr>(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    HttpClient http = factory.CreateClient(nameof(LlmVisionOcr));
    return new LlmVisionOcr(
        http,
        sp.GetRequiredService<LlmRuntimeConfig>(),
        sp.GetRequiredService<ILogger<LlmVisionOcr>>());
});

if (clusterConfig.Enabled)
{
    int localSlots = runtimeConfig.IsWeChatMode
        ? Math.Max(1, wechatEngine?.InstanceCount ?? runtimeConfig.WeChatInstances)
        : (engine?.EngineCount ?? runtimeConfig.EngineCount);
    int clusterCapacity = clusterConfig.EffectiveCapacity(
        localSlots,
        llmConfig.OcrConcurrency,
        runtimeConfig.IsLlmMode);
    string clusterModel = runtimeConfig.IsLlmMode
        ? llmConfig.Model
        : runtimeConfig.IsWeChatMode
            ? "wechat-" + (wechatEngine?.KindName ?? "plugin")
            : ClusterRuntimeConfig.LocalModelName;
    var clusterSelf = new ClusterSelf
    {
        NodeId = clusterConfig.NodeId,
        Role = clusterConfig.Role,
        Capacity = clusterCapacity,
        EngineCount = engine?.EngineCount ?? (runtimeConfig.IsWeChatMode ? localSlots : 0),
        OcrMode = runtimeConfig.Mode,
        Model = clusterModel,
        Dpi = runtimeConfig.DefaultDpi,
        AdvertiseUrl = clusterConfig.AdvertiseUrl,
        LlmConfigured = llmConfig.IsUsable,
        NerConcurrency = llmConfig.IsUsable && clusterConfig.DistributedNer
            ? llmConfig.MaxConcurrency
            : 0,
    };
    builder.Services.AddSingleton(clusterConfig);
    builder.Services.AddSingleton(clusterSelf);
    builder.Services.AddSingleton(sp =>
    {
        var registry = new ClusterNodeRegistry(
            clusterSelf,
            Math.Max(3_000, clusterConfig.HealthIntervalMs * 3));
        if (clusterConfig.IsCoordinator)
        {
            foreach (ClusterWorkerEndpoint worker in clusterConfig.Workers)
                registry.SeedWorker(worker.Url, worker.Capacity);
        }

        return registry;
    });
    builder.Services.AddHttpClient(ClusterCoordinator.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+cluster)");
    });
    builder.Services.AddSingleton<ClusterCoordinator>();
    Console.WriteLine(
        $"Cluster: enabled role={clusterConfig.Role} nodeId={clusterConfig.NodeId} " +
        $"capacity={clusterCapacity} model={clusterModel} dpi={runtimeConfig.DefaultDpi} " +
        $"workers={clusterConfig.Workers.Count} advertise={clusterConfig.AdvertiseUrl} " +
        $"coordinator={clusterConfig.CoordinatorUrl} verboseDispatch={(clusterConfig.VerboseDispatch ? "on" : "off")} " +
        $"distributedNer={(clusterConfig.DistributedNer ? "on" : "off")} token=(set)");

    // Nacos service discovery: register this node and pull worker list from Nacos.
    if (clusterConfig.UseNacos && clusterConfig.Nacos is not null)
    {
        builder.Services.AddHttpClient<NacosClient>("MiniOcr.Nacos", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+nacos)");
        });
        builder.Services.AddSingleton(sp =>
        {
            IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient http = factory.CreateClient("MiniOcr.Nacos");
            return new NacosClient(http, clusterConfig.Nacos, sp.GetRequiredService<ILogger<NacosClient>>());
        });
        Console.WriteLine(
            $"Nacos: enabled server={clusterConfig.Nacos.ServerAddr} service={clusterConfig.Nacos.ServiceName} " +
            $"group={clusterConfig.Nacos.GroupName} cluster={clusterConfig.Nacos.ClusterName} " +
            $"namespace={clusterConfig.Nacos.Namespace} auth={(clusterConfig.Nacos.HasAuth() ? "on" : "off")}");
    }
}
else
{
    Console.WriteLine(clusterConfig.DisabledReason is { Length: > 0 }
        ? "Cluster: off (" + clusterConfig.DisabledReason + ")"
        : "Cluster: off");
}

builder.Services.AddSingleton<PdfOcrPipeline>(sp =>
{
    OcrRuntimeConfig cfg = sp.GetRequiredService<OcrRuntimeConfig>();
    return new PdfOcrPipeline(
        cfg,
        sp.GetRequiredService<ILogger<PdfOcrPipeline>>(),
        engine: sp.GetService<OcrEngine>(),
        llm: sp.GetService<LlmEntityExtractor>(),
        vision: sp.GetService<LlmVisionOcr>(),
        wechat: sp.GetService<WeChatOcrEngine>(),
        cluster: sp.GetService<ClusterCoordinator>(),
        parallelRenderer: parallelRenderer);
});
if (clusterConfig.Enabled)
{
    builder.Services.AddSingleton<ClusterWorkerHost>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterCoordinator>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterWorkerHost>());
    if (clusterConfig.UseNacos)
    {
        builder.Services.AddSingleton<NacosHostedService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NacosHostedService>());
    }
}

builder.Services.AddSingleton(sp => new ChallengeJobService(
    sp.GetRequiredService<ParallelPdfDownloader>(),
    sp.GetRequiredService<PdfOcrPipeline>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILogger<ChallengeJobService>>(),
    sp.GetService<ClusterCoordinator>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChallengeJobService>());

WebApplication app = builder.Build();
if (clusterConfig.Enabled)
    ClusterEndpoints.Map(app);
ILogger logger = app.Logger;

IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    PdfOcrPipeline? pipeline = app.Services.GetService<PdfOcrPipeline>();
    if (pipeline is not null)
        pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult();
    if (engine is not null)
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    if (wechatEngine is not null)
        wechatEngine.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.MapGet("/health", (IServiceProvider sp) =>
{
    OcrEngine? ocr = sp.GetService<OcrEngine>();
    LlmRuntimeConfig llm = sp.GetRequiredService<LlmRuntimeConfig>();
    OcrRuntimeConfig cfg = sp.GetRequiredService<OcrRuntimeConfig>();
    return Results.Json(
        new HealthResponse
        {
            Status = "ok",
            ModelsLoaded = ocr?.IsLoaded ?? false,
            Runtime = RuntimeInformation.FrameworkDescription,
            ProcessorCount = cfg.ProcessorCount,
            EngineCount = ocr?.EngineCount ?? wechatEngine?.InstanceCount ?? 0,
            LineWorkerCount = ocr?.LineWorkerCount ?? 0,
            DetIntraOpThreads = ocr?.DetIntraOpThreads ?? 0,
            DefaultDpi = cfg.DefaultDpi,
            UseDirectionClassification = cfg.UseDirectionClassification,
            RasterWorkerCount = cfg.RasterWorkerCount,
            RenderMode = cfg.RenderMode,
            RenderProcessCount = cfg.RenderProcessCount,
            TextLayer = cfg.TextLayer,
            RecBatchLines = cfg.RecBatchLines,
            OcrBackend = ocr?.EffectiveBackend ?? cfg.Backend,
            RecIntraOpThreads = cfg.RecIntraOpThreads,
            VulkanDevice = ocr?.VulkanDeviceName ?? "",
            VulkanDeviceLocalBytes = ocr?.VulkanDeviceLocalBytes ?? 0,
            GpuDevice = ocr?.GpuDeviceName ?? "",
            GpuMemoryBytes = ocr?.GpuMemoryBytes ?? 0,
            DetLimitSideLength = cfg.DetLimitSideLength,
            AutoScaleFromCpu = cfg.AutoScaleFromCpu,
            ConfigPath = configPath,
            ConfigFileExisted = configFileExisted,
            ConfigPathSource = configLoad.PathSource,
            OcrMode = cfg.Mode,
            WeChatKind = wechatEngine?.KindName ?? "",
            WeChatPluginPath = wechatEngine?.PluginPath ?? "",
            WeChatDir = wechatEngine?.WeChatDir ?? "",
            WeChatInstances = wechatEngine?.InstanceCount ?? 0,
            WeChatStatus = wechatStatus,
            LlmOcrConcurrency = llm.OcrConcurrency,
            LlmEnabled = llm.Enabled,
            LlmUsable = llm.IsUsable,
            LlmModel = llm.Model,
            LlmBaseUrl = llm.BaseUrl,
            LlmFallbackToHeuristics = llm.FallbackToHeuristics,
            LlmApiKey = apiKeyStatus,
            Cluster = BuildClusterHealth(sp),
        },
        AppJsonContext.Default.HealthResponse);
});

async Task<IResult> HandleChallengeAsync(
    HttpRequest httpRequest,
    ChallengeJobService jobs,
    CancellationToken ct)
{
    ChallengeRequest? body;
    try
    {
        body = await httpRequest.ReadFromJsonAsync(AppJsonContext.Default.ChallengeRequest, ct)
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Challenge JSON parse failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Invalid JSON body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (body is null)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Empty body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (string.IsNullOrWhiteSpace(body.Key) ||
        string.IsNullOrWhiteSpace(body.CallbackUrl) ||
        body.Files is null ||
        body.Files.Count == 0)
    {
        return Results.Json(
            new ChallengeAckResponse
            {
                Ok = false,
                Error = "Required: key, callbackUrl, files[] (each with fileId + url).",
            },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (!Uri.TryCreate(body.CallbackUrl, UriKind.Absolute, out Uri? cb) ||
        (cb.Scheme != Uri.UriSchemeHttp && cb.Scheme != Uri.UriSchemeHttps))
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "callbackUrl must be absolute http(s)." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    List<ChallengeFileRef> files = [];
    foreach (ChallengeFileRef f in body.Files)
    {
        if (string.IsNullOrWhiteSpace(f.Url) || string.IsNullOrWhiteSpace(f.FileId))
            continue;
        if (!Uri.TryCreate(f.Url, UriKind.Absolute, out Uri? fu) ||
            (fu.Scheme != Uri.UriSchemeHttp && fu.Scheme != Uri.UriSchemeHttps))
            continue;
        files.Add(f);
    }

    if (files.Count == 0)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "No valid files (need fileId + http(s) url)." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    ChallengeJob job = new()
    {
        TeamId = body.TeamId,
        Key = body.Key.Trim(),
        CallbackUrl = body.CallbackUrl.Trim(),
        Files = files,
    };

    // Non-blocking enqueue when possible; if queue full, brief wait then still ack
    // (platform requires fast 200 — do not run OCR on this thread).
    if (!jobs.TryEnqueue(job))
    {
        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await jobs.EnqueueAsync(job, waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Challenge queue full; rejecting teamId={TeamId}, keyPresent=true, files={Count}",
                job.TeamId,
                job.Files.Count);
            return Results.Json(
                new ChallengeAckResponse { Ok = false, Error = "Server busy; retry shortly." },
                AppJsonContext.Default.ChallengeAckResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    logger.LogInformation(
        "Challenge accepted: teamId={TeamId}, keyPresent=true, files={Count}, queuedApprox={Queued}",
        job.TeamId,
        job.Files.Count,
        jobs.QueuedApprox);

    return Results.Json(
        new ChallengeAckResponse { Ok = true },
        AppJsonContext.Default.ChallengeAckResponse);
}

// Competition primary serviceUrl path (no auth).
app.MapPost("/challenge", HandleChallengeAsync);
// Also accept POST / so serviceUrl can be the bare base URL.
app.MapPost("/", HandleChallengeAsync);

app.MapPost("/ocr", async Task<IResult> (
    HttpRequest httpRequest,
    ParallelPdfDownloader downloader,
    PdfOcrPipeline pipeline,
    OcrRuntimeConfig config,
    IServiceProvider services,
    CancellationToken ct) =>
{
    // Sync debug OCR: competition-compatible request/response shapes (same builders as callback).
    OcrDebugRequest? body;
    try
    {
        body = await httpRequest.ReadFromJsonAsync(AppJsonContext.Default.OcrDebugRequest, ct)
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Debug /ocr JSON parse failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Invalid JSON body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (body is null)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Empty body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    List<ChallengeFileRef> files = [];
    if (body.Files is { Count: > 0 })
    {
        foreach (ChallengeFileRef f in body.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Url) || string.IsNullOrWhiteSpace(f.FileId))
                continue;
            if (!Uri.TryCreate(f.Url, UriKind.Absolute, out Uri? fu) ||
                (fu.Scheme != Uri.UriSchemeHttp && fu.Scheme != Uri.UriSchemeHttps))
                continue;
            files.Add(f);
        }
    }
    else if (!string.IsNullOrWhiteSpace(body.Url))
    {
        // Legacy { "url", "dpi" } → files:[{fileId:"f1",url}]
        if (!Uri.TryCreate(body.Url, UriKind.Absolute, out Uri? fu) ||
            (fu.Scheme != Uri.UriSchemeHttp && fu.Scheme != Uri.UriSchemeHttps))
        {
            return Results.Json(
                new ChallengeAckResponse { Ok = false, Error = "url must be absolute http(s)." },
                AppJsonContext.Default.ChallengeAckResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        files.Add(new ChallengeFileRef { FileId = "f1", Url = body.Url.Trim() });
    }

    int? dpi = body.Dpi;
    if (dpi is null &&
        httpRequest.Query.TryGetValue("dpi", out var dpiQuery) &&
        int.TryParse(dpiQuery.FirstOrDefault(), out int dpiFromQuery))
    {
        dpi = dpiFromQuery;
    }

    dpi ??= config.DefaultDpi;
    bool verbose = WantsVerbose(httpRequest);

    if (files.Count == 0 && !string.IsNullOrWhiteSpace(body.Path))
    {
        try
        {
            byte[] pdf = LocalPdfFile.ReadAllBytes(body.Path);
            using RentedBuffer buffer = LocalPdfFile.RentCopy(pdf);
            OcrResponse ocr = await pipeline
                .ProcessAsync(buffer, downloadMs: 0, downloadMode: "local-file", ct, dpi)
                .ConfigureAwait(false);
            return FinishDebug(body.TeamId, body.Key, "f1", ocr, verbose, config.Mode);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException)
        {
            return Results.Json(
                new ChallengeAckResponse { Ok = false, Error = ex.Message },
                AppJsonContext.Default.ChallengeAckResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Debug /ocr local path failed");
            return Results.Json(
                new ChallengeAckResponse { Ok = false, Error = "OCR failed: " + ex.Message },
                AppJsonContext.Default.ChallengeAckResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    if (files.Count == 0)
    {
        return Results.Json(
            new ChallengeAckResponse
            {
                Ok = false,
                Error =
                    "Required: files[{fileId,url}] (competition shape), legacy { url, dpi? }, " +
                    "or { path } (local PDF, non-ASCII paths ok). " +
                    "Optional: teamId, key, callbackUrl (ignored for sync). Add ?verbose=1 for page text and ms/page.",
            },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    List<ChallengeFileResult> results = [];
    OcrResponse? single = null;
    try
    {
        foreach (ChallengeFileRef file in files)
        {
            string fileId = file.FileId ?? "f1";
            string url = file.Url!;
            services.GetService<ClusterCoordinator>()?.BeginSourcePrefetch(url);
            ParallelPdfDownloader.DownloadResult download =
                await downloader.DownloadAsync(url, ct).ConfigureAwait(false);
            using (download.Buffer)
            {
                OcrResponse ocr = await pipeline
                    .ProcessAsync(download.Buffer, download.ElapsedMs, download.Mode, ct, dpi, url)
                    .ConfigureAwait(false);
                if (files.Count == 1)
                    single = ocr;
                results.Add(ChallengeResultMapper.BuildFileResult(fileId, ocr));
            }
        }
    }
    catch (ArgumentException ex)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Debug /ocr download failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Failed to download PDF: " + ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Debug /ocr pipeline failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "OCR failed: " + ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status500InternalServerError);
    }

    if (verbose && single is not null)
        return FinishDebug(body.TeamId, body.Key, files[0].FileId ?? "f1", single, verbose: true, config.Mode);

    ChallengeCallbackBody callbackShaped = new()
    {
        TeamId = body.TeamId,
        Key = string.IsNullOrWhiteSpace(body.Key) ? "debug" : body.Key.Trim(),
        Result = results,
    };
    return Results.Json(callbackShaped, AppJsonContext.Default.ChallengeCallbackBody);
});

app.MapPost("/ocr/upload", async Task<IResult> (
    HttpRequest httpRequest,
    PdfOcrPipeline pipeline,
    OcrRuntimeConfig config,
    CancellationToken ct) =>
{
    if (!httpRequest.HasFormContentType)
    {
        return Results.Json(
            new ChallengeAckResponse
            {
                Ok = false,
                Error = "POST /ocr/upload expects multipart/form-data with a file field, or a path field.",
            },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    IFormCollection form;
    try
    {
        form = await httpRequest.ReadFormAsync(ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Debug /ocr/upload form parse failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Invalid multipart body: " + ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    int? dpi = null;
    if (form.TryGetValue("dpi", out Microsoft.Extensions.Primitives.StringValues dpiForm) &&
        int.TryParse(dpiForm.FirstOrDefault(), out int dpiFromForm))
    {
        dpi = dpiFromForm;
    }

    if (dpi is null &&
        httpRequest.Query.TryGetValue("dpi", out Microsoft.Extensions.Primitives.StringValues dpiQuery) &&
        int.TryParse(dpiQuery.FirstOrDefault(), out int dpiFromQuery))
    {
        dpi = dpiFromQuery;
    }

    dpi ??= config.DefaultDpi;
    bool verbose = WantsVerbose(httpRequest);

    try
    {
        byte[] pdf;
        if (form.Files.Count > 0)
        {
            IFormFile file = form.Files[0];
            if (file.Length > LocalPdfFile.MaxBytes)
            {
                return Results.Json(
                    new ChallengeAckResponse { Ok = false, Error = "PDF exceeds 300 MB." },
                    AppJsonContext.Default.ChallengeAckResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            using var mem = new MemoryStream(file.Length > int.MaxValue ? 0 : (int)Math.Max(0, file.Length));
            await file.CopyToAsync(mem, ct).ConfigureAwait(false);
            pdf = mem.ToArray();
            if (!LocalPdfFile.LooksLikePdf(pdf))
            {
                return Results.Json(
                    new ChallengeAckResponse { Ok = false, Error = "Upload is not a PDF (missing %PDF header)." },
                    AppJsonContext.Default.ChallengeAckResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            logger.LogInformation(
                "OCR upload: fileName={Name} bytes={Bytes}",
                file.FileName,
                pdf.Length);
        }
        else if (form.TryGetValue("path", out Microsoft.Extensions.Primitives.StringValues pathVal) &&
                 !string.IsNullOrWhiteSpace(pathVal.FirstOrDefault()))
        {
            pdf = LocalPdfFile.ReadAllBytes(pathVal.ToString());
        }
        else
        {
            return Results.Json(
                new ChallengeAckResponse { Ok = false, Error = "Provide a file field or a path field." },
                AppJsonContext.Default.ChallengeAckResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        using RentedBuffer buffer = LocalPdfFile.RentCopy(pdf);
        OcrResponse ocr = await pipeline
            .ProcessAsync(buffer, downloadMs: 0, downloadMode: "upload", ct, dpi)
            .ConfigureAwait(false);
        string fileId = "f1";
        if (form.TryGetValue("fileId", out Microsoft.Extensions.Primitives.StringValues idVal) &&
            !string.IsNullOrWhiteSpace(idVal.FirstOrDefault()))
        {
            fileId = idVal.ToString();
        }

        return FinishDebug(teamId: 0, key: "debug", fileId, ocr, verbose, config.Mode);
    }
    catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Debug /ocr/upload failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "OCR failed: " + ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/", () => Results.Text(
    "MiniOcr AOT API\n" +
    "POST /challenge  ← competition serviceUrl (also POST /)\n" +
    "  {\"teamId\":123,\"key\":\"...\",\"callbackUrl\":\"https://...\",\"files\":[{\"fileId\":\"f1\",\"url\":\"https://...pdf\"}]}\n" +
    "  → HTTP 200 {\"ok\":true} immediately; results POSTed async to callbackUrl\n" +
    "POST /ocr        debug sync OCR (competition shapes; response = callback body)\n" +
    "  {\"teamId\":0,\"key\":\"debug\",\"files\":[{\"fileId\":\"f1\",\"url\":\"https://...pdf\"}]}\n" +
    "  or legacy {\"url\":\"https://.../file.pdf\"} or {\"path\":\"C:\\\\...\\\\file.pdf\"} / ?dpi=96\n" +
    "  ?verbose=1 (or ?text=1) returns per-page text and ms/page instead of the callback shape\n" +
    "POST /ocr/upload multipart file field, or form field path= (local PDF, Unicode paths ok)\n" +
    "GET  /health\n" +
    "CLI  MiniOcr --compare <pdf> [--pages N] [--dpi N]   wechat vs local → wechat-vs-local.txt\n" +
    $"Config: path={configPath} existed={configFileExisted} source={configLoad.PathSource} " +
    $"ocr.mode={runtimeConfig.Mode} wechat={wechatStatus} llm.usable={llmConfig.IsUsable} apiKey={apiKeyStatus}\n" +
    "Env CONFIG: MINIOCR_CONFIG_PATH\n" +
    "Env OCR: MINIOCR_OCR_MODE MINIOCR_OCR_BACKEND MINIOCR_OCR_VULKAN_DEVICE MINIOCR_ENGINES MINIOCR_DPI MINIOCR_LINE_WORKERS MINIOCR_DET_THREADS MINIOCR_REC_INTRA_OP_THREADS MINIOCR_USE_CLS MINIOCR_RASTER_WORKERS MINIOCR_RENDER_MODE MINIOCR_RENDER_PROCESSES\n" +
    "Env WECHAT: MINIOCR_WECHAT_OCR_PATH MINIOCR_WECHAT_DIR MINIOCR_WECHAT_INSTANCES MINIOCR_WECHAT_FALLBACK\n" +
    "Env LLM: MINIOCR_LLM_API_KEY MINIOCR_LLM_BASE_URL MINIOCR_LLM_MODEL MINIOCR_LLM_MAX_CONCURRENCY MINIOCR_LLM_PAGES_PER_REQUEST MINIOCR_LLM_OCR_CONCURRENCY MINIOCR_LLM_THINKING\n" +
    "Env cluster: MINIOCR_CLUSTER_ENABLED MINIOCR_CLUSTER_ROLE MINIOCR_CLUSTER_TOKEN MINIOCR_CLUSTER_NODE_ID MINIOCR_CLUSTER_ADVERTISE_URL MINIOCR_CLUSTER_COORDINATOR_URL MINIOCR_CLUSTER_WORKERS MINIOCR_CLUSTER_CAPACITY MINIOCR_CLUSTER_VERBOSE_DISPATCH MINIOCR_CLUSTER_DISTRIBUTED_NER\n" +
    "  Each claim, heartbeat, batch completion, and empty poll is Debug unless MINIOCR_CLUSTER_VERBOSE_DISPATCH=1 (or cluster.verboseDispatch). Progress summaries stay Information. Logging__LogLevel__MiniOcr.Services.ClusterCoordinator=Debug (and ClusterWorkerHost) shows the same detail. Per-request framework logs default to Warning; raise Logging__LogLevel__Microsoft.AspNetCore and Logging__LogLevel__System.Net.Http.HttpClient to see them.\n",
    "text/plain; charset=utf-8"));

string urls = string.Join(", ", app.Urls.DefaultIfEmpty("(default http://localhost:5000)"));
logger.LogInformation(
    "MiniOcr ready — mode={Mode}, ProcessorCount={Cores}, engines={Engines}, lineWorkers={LineWorkers}, det={Det}, raster={Raster}, dpi={Dpi}, useCls={UseCls}, llmUsable={Llm}, ocrConcurrency={OcrConc}, listening={Urls}",
    runtimeConfig.Mode,
    runtimeConfig.ProcessorCount,
    engine?.EngineCount ?? 0,
    engine?.LineWorkerCount ?? 0,
    engine?.DetIntraOpThreads ?? 0,
    runtimeConfig.RasterWorkerCount,
    runtimeConfig.DefaultDpi,
    runtimeConfig.UseDirectionClassification,
    llmConfig.IsUsable,
    llmConfig.OcrConcurrency,
    urls);

static void QuietFrameworkRequestLogs(WebApplicationBuilder webBuilder, string category, LogLevel level)
{
    string? configured = webBuilder.Configuration["Logging:LogLevel:" + category];
    if (!string.IsNullOrWhiteSpace(configured))
        return;
    webBuilder.Logging.AddFilter(category, level);
}

static string FirstLine(string message)
{
    int cut = message.IndexOfAny(['\r', '\n']);
    string line = cut < 0 ? message : message[..cut];
    return line.Length <= 240 ? line : line[..240];
}

static bool WantsVerbose(HttpRequest request) =>
    request.Query.ContainsKey("verbose") || request.Query.ContainsKey("text");

static IResult FinishDebug(int teamId, string? key, string fileId, OcrResponse ocr, bool verbose, string mode)
{
    if (verbose)
    {
        return Results.Json(
            OcrTextDebug.From(ocr, mode),
            AppJsonContext.Default.OcrTextDebugResponse);
    }

    ChallengeCallbackBody callbackShaped = new()
    {
        TeamId = teamId,
        Key = string.IsNullOrWhiteSpace(key) ? "debug" : key.Trim(),
        Result = [ChallengeResultMapper.BuildFileResult(fileId, ocr)],
    };
    return Results.Json(callbackShaped, AppJsonContext.Default.ChallengeCallbackBody);
}

ClusterHealthInfo? BuildClusterHealth(IServiceProvider sp)
{
    ClusterRuntimeConfig? cfg = sp.GetService<ClusterRuntimeConfig>();
    if (cfg is not { Enabled: true })
        return null;
    if (cfg.IsCoordinator && sp.GetService<ClusterCoordinator>() is { } coordinator)
        return coordinator.BuildHealth();
    if (sp.GetService<ClusterWorkerHost>() is { } worker)
        return worker.BuildHealth();
    return null;
}

await app.RunAsync();
