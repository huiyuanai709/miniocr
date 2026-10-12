using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MiniOcr;
using MiniOcr.Models;
using MiniOcr.Services;
using SkiaSharp;

int failed = 0;

void Assert(bool cond, string msg)
{
    Console.WriteLine(cond ? "  PASS  " + msg : "  FAIL  " + msg);
    if (!cond)
        failed++;
}

string trailing = """{"companies":["甲公司",],"persons":["张伟",]}""";
LlmEntityPayload? entity = LlmJson.Deserialize<LlmEntityPayload>(trailing);
Assert(entity?.Companies is ["甲公司"] && entity.Persons is ["张伟"], "source-gen parse allows trailing commas");

string commented = """{ /*note*/ "companies": [], "persons": ["李娜"] }""";
LlmEntityPayload? commentedPayload = LlmJson.Deserialize<LlmEntityPayload>(commented);
Assert(commentedPayload?.Persons is ["李娜"], "source-gen parse skips comments");

string fenced = NerPrompt.ExtractJsonObject("```json\n{\"companies\":[\"A\"],\"persons\":[]}\n```");
LlmEntityPayload? fromFence = LlmJson.Deserialize<LlmEntityPayload>(fenced);
Assert(fromFence?.Companies is ["A"], "fence strip then source-gen parse");

var request = new ChatCompletionRequest
{
    Model = "deepseek-chat",
    Temperature = 0,
    ResponseFormat = LlmJson.JsonObject,
    Messages = [new ChatMessage { Role = "user", Content = "return json" }],
};
string wire = JsonSerializer.Serialize(request, AppJsonContext.Default.ChatCompletionRequest);
Assert(wire.Contains("\"response_format\"", StringComparison.Ordinal), "wire field is response_format");
Assert(wire.Contains("\"json_object\"", StringComparison.Ordinal), "wire type is json_object");
Assert(!wire.Contains("responseFormat", StringComparison.Ordinal), "camelCase name is not sent");

ChallengeCallbackBody callback = new()
{
    TeamId = 7,
    Key = "k",
    Result =
    [
        new ChallengeFileResult
        {
            FileId = "f1",
            Pages =
            [
                new ChallengePageResult
                {
                    Page = 1,
                    RuleList =
                    [
                        new ChallengeRule
                        {
                            RuleCode = "B06",
                            RuleName = "公司名称",
                            RuleItemList =
                            [
                                new ChallengeRuleItem
                                {
                                    CompanyName = "北京华腾科技有限公司",
                                    Count = 1,
                                    OriginText = ["甲方：北京华腾科技有限公司"],
                                },
                            ],
                        },
                    ],
                },
            ],
        },
    ],
};
string callbackJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
    callback, AppJsonContext.Relaxed.ChallengeCallbackBody));
Assert(callbackJson.Contains("北京华腾科技有限公司", StringComparison.Ordinal), "callback writes Chinese as UTF-8");
Assert(callbackJson.Contains("公司名称", StringComparison.Ordinal), "callback rule name stays UTF-8");
Assert(callbackJson.Contains("\"ruleName\":\"公司名称\"", StringComparison.Ordinal), "callback keeps camelCase ruleName");
Assert(callbackJson.Contains("\"companyName\":\"北京华腾科技有限公司\"", StringComparison.Ordinal), "callback keeps camelCase companyName");
Assert(!callbackJson.Contains("\\u", StringComparison.Ordinal), "callback does not use \\u escapes");
string defaultJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
    callback, AppJsonContext.Default.ChallengeCallbackBody));
Assert(defaultJson.Contains("\\u", StringComparison.Ordinal), "default context still escapes non-ASCII");

JsonSerializerOptions httpOptions = new()
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    TypeInfoResolver = AppJsonContext.Relaxed,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};
string httpJson = JsonSerializer.Serialize(new ChallengeAckResponse { Ok = false, Error = "文件不存在" }, httpOptions);
Assert(httpJson.Contains("文件不存在", StringComparison.Ordinal), "HTTP JSON options keep Chinese as UTF-8");
Assert(!httpJson.Contains("\\u", StringComparison.Ordinal), "HTTP JSON options do not use \\u escapes");

Microsoft.AspNetCore.Http.Json.JsonOptions aspNet = new();
aspNet.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
aspNet.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Relaxed);
string aspNetJson = JsonSerializer.Serialize(
    new ChallengeAckResponse { Ok = false, Error = "文件不存在" }, aspNet.SerializerOptions);
Assert(aspNetJson.Contains("文件不存在", StringComparison.Ordinal), "ASP.NET JsonOptions keep Chinese as UTF-8");
Assert(!aspNetJson.Contains("\\u", StringComparison.Ordinal), "ASP.NET JsonOptions do not use \\u escapes");
Assert(aspNetJson.Contains("\"ok\":", StringComparison.Ordinal), "ASP.NET JsonOptions keep camelCase");

Assert(LlmJson.IsFormatRejected(400, "{\"error\":\"unsupported response_format\"}"), "400 mentioning response_format is a rejection");
Assert(!LlmJson.IsFormatRejected(500, "response_format"), "non-400 is not a format rejection");
Assert(!LlmJson.IsFormatRejected(400, "model not found"), "unrelated 400 stays a hard error");
Assert(LlmJson.IsParseFailure(new JsonException("bad")), "JsonException is a local retry");
Assert(!LlmJson.IsParseFailure(new HttpRequestException("timeout")), "transport errors are not parse retries");

Console.WriteLine("single-node group waits for the next page head");
CaptureHandler capture = new();
using HttpClient http = new(capture) { BaseAddress = new Uri("http://llm.test") };
LlmEntityExtractor extractor = new(http, new LlmRuntimeConfig
{
    Enabled = true,
    BaseUrl = "http://llm.test",
    ApiKey = "test-key",
    Model = "test-model",
    TimeoutSeconds = 30,
    PagesPerRequest = 1,
    MaxCharsPerRequest = 100_000,
    MaxConcurrency = 2,
    JsonObject = true,
}, new QuietLog());
LlmEntityExtractor.LlmExtractionSession session = extractor.Begin(3, CancellationToken.None);
string page2 = "腾科技有限公司法定代表人张伟" + new string('。', 40);
session.Add(new OcrPageResult { Page = 1, Text = "甲方北京华" });
session.Add(new OcrPageResult { Page = 2, Text = "   " });
await Task.Delay(200);
Assert(capture.Bodies.Count == 0, "page 1 waits through a blank page; NER has not started");
session.Add(new OcrPageResult { Page = 3, Text = page2 });
string firstBody = UserText(await capture.WaitForBodyAsync(0));
Assert(firstBody.Contains("甲方北京华", StringComparison.Ordinal), "the first request is page 1");
Assert(firstBody.Contains("下一页开头", StringComparison.Ordinal), "single-node prompt marks the next-page head");
Assert(firstBody.Contains("腾科技有限公司", StringComparison.Ordinal), "lookahead carries the split company tail");
Assert(!firstBody.Contains("page 2", StringComparison.Ordinal), "the blank page is not a lookahead source");
OcrEntities entities = (await session.CompleteAsync()).Entities;
Assert(capture.Count == 2, "the trailing group is sent when the document ends");
string secondBody = UserText(capture.Bodies[1]);
Assert(secondBody.Contains("腾科技有限公司", StringComparison.Ordinal), "the last page is still sent");
Assert(!secondBody.Contains("下一页开头", StringComparison.Ordinal), "the last group has no further page");
Assert(entities.Companies.Count == 0 && entities.Persons.Count == 0, "empty model output stays empty");

CaptureHandler ordered = new();
using HttpClient http2 = new(ordered) { BaseAddress = new Uri("http://llm.test") };
LlmEntityExtractor late = new(http2, new LlmRuntimeConfig
{
    Enabled = true,
    BaseUrl = "http://llm.test",
    ApiKey = "test-key",
    Model = "test-model",
    TimeoutSeconds = 30,
    PagesPerRequest = 1,
    MaxCharsPerRequest = 100_000,
    MaxConcurrency = 2,
    JsonObject = true,
}, new QuietLog());
LlmEntityExtractor.LlmExtractionSession outOfOrder = late.Begin(2, CancellationToken.None);
outOfOrder.Add(new OcrPageResult { Page = 2, Text = page2 });
await Task.Delay(200);
Assert(ordered.Bodies.Count == 0, "a later page does not start NER before page 1");
outOfOrder.Add(new OcrPageResult { Page = 1, Text = "甲方北京华" });
string joined = UserText(await ordered.WaitForBodyAsync(0));
Assert(joined.Contains("甲方北京华", StringComparison.Ordinal) && joined.Contains("腾科技有限公司", StringComparison.Ordinal),
    "page 1, arriving second, still sees page 2's head");
await outOfOrder.CompleteAsync();
Assert(ordered.Count == 2, "both pages are sent once, including the page that arrived early");

// Direction A: the model answers with per-page ruleList; its count and originText are kept.
CaptureHandler pagesHandler = new()
{
    ResponseJson = """{"choices":[{"message":{"role":"assistant","content":"{\"pages\":[{\"page\":1,\"ruleList\":[{\"ruleCode\":\"B06\",\"ruleItemList\":[{\"companyName\":\"成都交子商圈物业服务有限公司\",\"count\":2,\"originText\":[\"正本成都交子商圈物业服务有限公司2026年度\"]}]}]}]}"}}]}""",
};
using HttpClient pagesHttp = new(pagesHandler) { BaseAddress = new Uri("http://llm.test") };
LlmEntityExtractor pagesExtractor = new(pagesHttp, new LlmRuntimeConfig
{
    Enabled = true,
    BaseUrl = "http://llm.test",
    ApiKey = "test-key",
    Model = "test-model",
    TimeoutSeconds = 30,
    PagesPerRequest = 1,
    MaxCharsPerRequest = 100_000,
    MaxConcurrency = 1,
    JsonObject = true,
}, new QuietLog());
LlmExtractionResult pagesResult = await pagesExtractor.ExtractAsync(
    [new OcrPageResult { Page = 1, Text = "正本成都交子商圈物业服务有限公司2026年度" }],
    CancellationToken.None);
Assert(pagesResult.Pages.Count == 1 && pagesResult.Pages[0].Page == 1, "per-page rules are returned");
ChallengeRuleItem modelItem = pagesResult.Pages[0].RuleList[0].RuleItemList[0];
Assert(modelItem.CompanyName == "成都交子商圈物业服务有限公司" && modelItem.Count == 2,
    "model count and name are kept (no exact-match recompute)");
Assert(pagesResult.Entities.Companies.Count == 0 && pagesResult.Entities.Persons.Count == 0,
    "entities are left for the pipeline to derive from the page rules");
string pagesBody = UserText(pagesHandler.Bodies[0]);
Assert(pagesBody.Contains("--- page 1 ---", StringComparison.Ordinal), "per-page prompt still gets the page marker");

// Direction A on the distributed path: per-page model rules become hits with the model's own count.
List<ClusterNerEntityHit> pageHits = ClusterNerAssembler.BuildFromPages(
    [new OcrPageResult { Page = 1, Text = "x" }],
    [
        new ChallengePageResult
        {
            Page = 1,
            RuleList =
            [
                new ChallengeRule
                {
                    RuleCode = "B06",
                    RuleItemList =
                    [
                        new ChallengeRuleItem
                        {
                            CompanyName = "成都交子商圈物业服务有限公司",
                            Count = 2,
                            OriginText = ["正本成都交子商圈物业服务有限公司"],
                        },
                    ],
                },
            ],
        },
        new ChallengePageResult
        {
            Page = 99,
            RuleList =
            [
                new ChallengeRule
                {
                    RuleCode = "B06",
                    RuleItemList = [new ChallengeRuleItem { CompanyName = "别家公司", Count = 5, OriginText = ["别家公司"] }],
                },
            ],
        },
    ]);
Assert(pageHits.Count == 1 && pageHits[0].Page == 1 && pageHits[0].Count == 2,
    "only assigned pages become hits and the model count is kept");
OcrEntities fromHits = ClusterNerAssembler.EntitiesFromHits(pageHits);
Assert(fromHits.Companies.Count == 1 && fromHits.Companies[0].Count == 2, "entity count follows the model");
List<ChallengePageResult> roundTrip = ClusterNerAssembler.PagesFromHits(pageHits);
Assert(roundTrip.Count == 1 && roundTrip[0].Page == 1 && roundTrip[0].RuleList[0].RuleItemList[0].Count == 2,
    "hits round-trip back to per-page rules with the model count");

AppConfigFile? redOff = JsonSerializer.Deserialize(
    """{"ocr":{"removeRedSeal":false}}""", AppJsonContext.Default.AppConfigFile);
Assert(redOff?.Ocr?.RemoveRedSeal == false, "config json removeRedSeal false");
AppConfigFile? redOmitted = JsonSerializer.Deserialize(
    """{"ocr":{"mode":"local"}}""", AppJsonContext.Default.AppConfigFile);
Assert(redOmitted?.Ocr?.RemoveRedSeal == true, "omitted removeRedSeal stays true");

using SKBitmap grayPage = new(4, 4, SKColorType.Gray8, SKAlphaType.Opaque);
Assert(RedSealFilter.Apply(grayPage) == 0, "Gray8 pages skip red-seal removal");
using SKBitmap colorPage = new(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
colorPage.Erase(new SKColor(234, 232, 230));
colorPage.SetPixel(4, 4, new SKColor(220, 40, 30, 255));
Assert(RedSealFilter.Apply(colorPage) >= 1, "BGRA red seal pixel is replaced before detection");
SKColor kept = colorPage.GetPixel(4, 4);
Assert(kept.Red == 234 && kept.Green == 232 && kept.Blue == 230, "BGRA red becomes the page paper color");

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    return 1;
}

Console.WriteLine("ALL PASSED");
return 0;

static string UserText(string body)
{
    ChatCompletionRequest? request = JsonSerializer.Deserialize(body, AppJsonContext.Default.ChatCompletionRequest);
    if (request is null || request.Messages.Count == 0)
        return body;
    return request.Messages[^1].Content ?? body;
}

sealed class CaptureHandler : HttpMessageHandler
{
    private readonly List<string> _bodies = [];
    private readonly List<TaskCompletionSource<string>> _waiters = [];

    public string ResponseJson { get; set; } =
        """{"choices":[{"message":{"role":"assistant","content":"{\"companies\":[],\"persons\":[]}"}}]}""";

    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_bodies)
                return _bodies.ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (_bodies)
                return _bodies.Count;
        }
    }

    public async Task<string> WaitForBodyAsync(int index)
    {
        TaskCompletionSource<string> ready;
        lock (_bodies)
        {
            if (index < _bodies.Count)
                return _bodies[index];
            while (_waiters.Count <= index)
                _waiters.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
            ready = _waiters[index];
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        return await ready.Task.WaitAsync(timeout.Token);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        lock (_bodies)
        {
            _bodies.Add(body);
            int index = _bodies.Count - 1;
            if (index < _waiters.Count)
                _waiters[index].TrySetResult(body);
        }

        string json = ResponseJson;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}

sealed class QuietLog : ILogger<LlmEntityExtractor>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}
