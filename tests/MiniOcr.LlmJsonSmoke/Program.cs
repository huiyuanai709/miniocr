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
Assert(callbackJson.Contains("\"count\":1", StringComparison.Ordinal), "callback count stays a JSON number");
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

Console.WriteLine("string page / count coercion and NER retries");
string stringPage = """{"pages":[{"page":"26","ruleList":[{"ruleCode":"B06","ruleItemList":[{"companyName":"甲公司","count":"2","originText":["甲公司"]}]}]}]}""";
LlmPagesPayload? coerced = LlmJson.Deserialize<LlmPagesPayload>(stringPage);
Assert(coerced?.Pages is { Count: 1 } && coerced.Pages[0].Page == 26, "string page coerces to int 26");
Assert(coerced!.Pages![0].RuleList![0].RuleItemList[0].Count == 2, "string count coerces to int 2");
string numericPage = """{"pages":[{"page":26,"ruleList":[{"ruleCode":"B04","ruleItemList":[{"personName":"张伟","count":3,"originText":["张伟"]}]}]}]}""";
LlmPagesPayload? numeric = LlmJson.Deserialize<LlmPagesPayload>(numericPage);
Assert(numeric?.Pages?[0].Page == 26 && numeric.Pages[0].RuleList?[0].RuleItemList[0].Count == 3,
    "numeric page and count still deserialize");
bool badPageThrew = false;
try
{
    LlmJson.Deserialize<LlmPagesPayload>("""{"pages":[{"page":"abc"}]}""");
}
catch (JsonException)
{
    badPageThrew = true;
}
Assert(badPageThrew, "non-numeric page string still fails parse");

string? prevAttempts = Environment.GetEnvironmentVariable("MINIOCR_LLM_NER_MAX_ATTEMPTS");
try
{
    Environment.SetEnvironmentVariable("MINIOCR_LLM_NER_MAX_ATTEMPTS", null);
    LlmRuntimeConfig defaults = AppConfigStore.ResolveLlm(new AppConfigFile { Llm = new LlmFileConfig() });
    Assert(defaults.NerMaxAttempts == 3, "default NER attempts is 3");
    LlmRuntimeConfig fromFile = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { NerMaxAttempts = 4 },
    });
    Assert(fromFile.NerMaxAttempts == 4, "config nerMaxAttempts is kept");
    Environment.SetEnvironmentVariable("MINIOCR_LLM_NER_MAX_ATTEMPTS", "5");
    LlmRuntimeConfig fromEnv = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { NerMaxAttempts = 4 },
    });
    Assert(fromEnv.NerMaxAttempts == 5, "MINIOCR_LLM_NER_MAX_ATTEMPTS overrides the file");
    Environment.SetEnvironmentVariable("MINIOCR_LLM_NER_MAX_ATTEMPTS", "99");
    LlmRuntimeConfig clamped = AppConfigStore.ResolveLlm(new AppConfigFile { Llm = new LlmFileConfig() });
    Assert(clamped.NerMaxAttempts == 8, "NER attempts clamp at 8");
}
finally
{
    Environment.SetEnvironmentVariable("MINIOCR_LLM_NER_MAX_ATTEMPTS", prevAttempts);
}

const string StringPagePayload =
    """{"pages":[{"page":"26","ruleList":[{"ruleCode":"B06","ruleName":"公司名称","ruleItemList":[{"companyName":"成都交子商圈物业服务有限公司","count":"2","originText":["正本成都交子商圈物业服务有限公司"]}]}]}]}""";
ScriptedHandler distributed = new();
distributed.EnqueueOk(Assistant(StringPagePayload));
using (HttpClient distHttp = new(distributed) { BaseAddress = new Uri("http://llm.test") })
{
    LlmEntityExtractor distExtractor = MakeExtractor(distHttp, attempts: 3);
    ClusterNerResultRequest distResult = await distExtractor.ExtractDistributedAsync(
        new ClusterNerAssignment
        {
            Kind = ClusterNerClaimKind.Group,
            GroupId = "g26",
            Pages = [26],
            Bodies = [new OcrPageResult { Page = 26, Text = "正本成都交子商圈物业服务有限公司" }],
            PromptText = "--- page 26 ---\n正本成都交子商圈物业服务有限公司",
        },
        CancellationToken.None);
    Assert(distributed.Calls == 1, "string page succeeds on the first distributed NER call");
    Assert(distResult.Entities is { Count: 1 }
        && distResult.Entities[0].Page == 26
        && distResult.Entities[0].Count == 2
        && distResult.Entities[0].Kind == "company",
        "distributed NER keeps the coerced page and count");
}

ListLog parseLog = new();
ScriptedHandler parseFails = new();
string broken = Assistant("{");
parseFails.EnqueueOk(broken);
parseFails.EnqueueOk(broken);
parseFails.EnqueueOk(broken);
using (HttpClient parseHttp = new(parseFails) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? parseError = await TryExtract(MakeExtractor(parseHttp, attempts: 3, parseLog));
    Assert(parseError is JsonException or InvalidOperationException,
        $"truncated JSON still throws after the correction budget ({parseError?.GetType().Name}: {parseError?.Message})");
    Assert(parseFails.Calls == 3, $"parse failure uses 3 attempts (calls={parseFails.Calls})");
    Assert(!IsThinkingCorrection(parseFails.RequestBodies[0]), "the first call is the original prompt");
    Assert(IsThinkingCorrection(parseFails.RequestBodies[1]) && IsThinkingCorrection(parseFails.RequestBodies[2]),
        "later parse attempts are thinking corrections, not the same prompt");
    Assert(ReadRequest(parseFails.RequestBodies[1]).Messages.Exists(m => (m.Content ?? "").Contains("{", StringComparison.Ordinal) && m.Role == "assistant"),
        "the correction quotes the bad assistant content");
    Assert(parseLog.Lines.Exists(l => l.Contains("attempt 1/3", StringComparison.Ordinal) && l.Contains("thinking correction", StringComparison.Ordinal)),
        "attempt 1/3 is logged as a thinking correction");
    Assert(parseLog.Lines.Exists(l => l.Contains("attempt 2/3", StringComparison.Ordinal) && l.Contains("thinking correction", StringComparison.Ordinal)),
        "attempt 2/3 is logged as a thinking correction");
    Assert(parseLog.Lines.Exists(l => l.Contains("attempt=3/3", StringComparison.Ordinal) && l.Contains("correction=yes", StringComparison.Ordinal)),
        "the last request is logged as a correction");
    Assert(!parseLog.Lines.Exists(l => l.Contains("attempt 3/3", StringComparison.Ordinal) && l.Contains("thinking correction", StringComparison.Ordinal)),
        "the exhausted attempt does not schedule another correction");
}

ScriptedHandler repaired = new();
string badPage = """{"pages":[{"page":"abc","ruleList":[]}]}""";
repaired.EnqueueOk(Assistant(badPage));
repaired.EnqueueOk(Assistant(StringPagePayload));
using (HttpClient repairHttp = new(repaired) { BaseAddress = new Uri("http://llm.test") })
{
    LlmExtractionResult repairedResult = await MakeExtractor(repairHttp, attempts: 3).ExtractAsync(
        [new OcrPageResult { Page = 26, Text = "正本成都交子商圈物业服务有限公司" }],
        CancellationToken.None);
    Assert(repaired.Calls == 2, $"type-wrong JSON is corrected on the next attempt (calls={repaired.Calls})");
    Assert(repairedResult.Pages.Count == 1 && repairedResult.Pages[0].Page == 26,
        "the thinking correction is parsed as the page result");
    ChatCompletionRequest first = ReadRequest(repaired.RequestBodies[0]);
    ChatCompletionRequest second = ReadRequest(repaired.RequestBodies[1]);
    Assert(first.Thinking?.Type == "disabled", "the first call keeps configured thinking off");
    Assert(second.Thinking?.Type == "enabled", "the correction forces thinking on");
    Assert(second.Messages.Exists(m => m.Role == "assistant" && (m.Content ?? "").Contains("\"page\":\"abc\"", StringComparison.Ordinal)),
        "the correction includes the bad response as the assistant turn");
    Assert(second.Messages.Exists(m => m.Role == "user" && (m.Content ?? "").Contains("错误输出", StringComparison.Ordinal) && (m.Content ?? "").Contains("\"page\":\"abc\"", StringComparison.Ordinal)),
        "the correction user message quotes the bad body");
}

ScriptedHandler oneShot = new();
oneShot.EnqueueOk(Assistant("{"));
using (HttpClient oneHttp = new(oneShot) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? once = await TryExtract(MakeExtractor(oneHttp, attempts: 1));
    Assert(once is not null && oneShot.Calls == 1, $"NerMaxAttempts=1 does not retry a parse failure (calls={oneShot.Calls})");
}

ListLog forbiddenLog = new();
ScriptedHandler forbidden = new();
forbidden.EnqueueStatus(HttpStatusCode.Forbidden, "{\"error\":\"bad key\"}");
forbidden.EnqueueOk(Assistant("""{"companies":[],"persons":[]}"""));
using (HttpClient forbidHttp = new(forbidden) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? denied = await TryExtract(MakeExtractor(forbidHttp, attempts: 3, forbiddenLog));
    Assert(denied is null && forbidden.Calls == 2, $"403 is retried (calls={forbidden.Calls}, {denied?.Message})");
    Assert(forbiddenLog.Lines.Exists(l => l.Contains("retrying", StringComparison.Ordinal) && l.Contains("403", StringComparison.Ordinal)),
        "403 log says retrying");
    Assert(!forbiddenLog.Lines.Exists(l => l.Contains("not retrying", StringComparison.Ordinal)),
        "403 is not treated as a hard auth failure");
}

ScriptedHandler always403 = new();
always403.EnqueueStatus(HttpStatusCode.Forbidden, "{\"error\":\"bad key\"}");
always403.EnqueueStatus(HttpStatusCode.Forbidden, "{\"error\":\"bad key\"}");
always403.EnqueueStatus(HttpStatusCode.Forbidden, "{\"error\":\"bad key\"}");
using (HttpClient alwaysForbidHttp = new(always403) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? denied = await TryExtract(MakeExtractor(alwaysForbidHttp, attempts: 3));
    Assert(always403.Calls == 3 && denied is HttpRequestException { StatusCode: HttpStatusCode.Forbidden },
        $"exhausted 403 still throws (calls={always403.Calls}, {denied?.GetType().Name}: {denied?.Message})");
}

ListLog authLog = new();
ScriptedHandler unauthorized = new();
unauthorized.EnqueueStatus(HttpStatusCode.Unauthorized, "{\"error\":\"no\"}");
using (HttpClient unauthHttp = new(unauthorized) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? auth = await TryExtract(MakeExtractor(unauthHttp, attempts: 3, authLog));
    Assert(unauthorized.Calls == 1 && auth is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized },
        $"401 is not retried (calls={unauthorized.Calls}, {auth?.Message})");
    Assert(authLog.Lines.Exists(l => l.Contains("not retrying", StringComparison.Ordinal) && l.Contains("401", StringComparison.Ordinal)),
        "401 log says not retrying");
}

ScriptedHandler badRequest = new();
badRequest.EnqueueStatus(HttpStatusCode.BadRequest, "{\"error\":\"model not found\"}");
using (HttpClient badHttp = new(badRequest) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? bad = await TryExtract(MakeExtractor(badHttp, attempts: 3));
    Assert(badRequest.Calls == 1 && bad is HttpRequestException { StatusCode: HttpStatusCode.BadRequest },
        $"400 is not retried (calls={badRequest.Calls}, {bad?.Message})");
}

string emptyEntities = Assistant("""{"companies":[],"persons":[]}""");
ScriptedHandler rateLimit = new();
rateLimit.EnqueueStatus(HttpStatusCode.TooManyRequests, "{\"error\":\"slow down\"}");
rateLimit.EnqueueOk(emptyEntities);
using (HttpClient rateHttp = new(rateLimit) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? rate = await TryExtract(MakeExtractor(rateHttp, attempts: 3));
    Assert(rate is null && rateLimit.Calls == 2, $"429 is retried once (calls={rateLimit.Calls}, {rate?.Message})");
}

ScriptedHandler timeoutStatus = new();
timeoutStatus.EnqueueStatus(HttpStatusCode.RequestTimeout, "{\"error\":\"slow\"}");
timeoutStatus.EnqueueOk(emptyEntities);
using (HttpClient timeoutHttp = new(timeoutStatus) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? timeout = await TryExtract(MakeExtractor(timeoutHttp, attempts: 3));
    Assert(timeout is null && timeoutStatus.Calls == 2, $"408 is retried (calls={timeoutStatus.Calls}, {timeout?.Message})");
}

ScriptedHandler server = new();
server.EnqueueStatus(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
server.EnqueueStatus(HttpStatusCode.BadGateway, "{\"error\":\"gw\"}");
server.EnqueueOk(emptyEntities);
using (HttpClient serverHttp = new(server) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? serverError = await TryExtract(MakeExtractor(serverHttp, attempts: 3));
    Assert(serverError is null && server.Calls == 3, $"5xx is retried through the third attempt (calls={server.Calls}, {serverError?.Message})");
}

ScriptedHandler always500 = new();
always500.EnqueueStatus(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
always500.EnqueueStatus(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
always500.EnqueueStatus(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
using (HttpClient alwaysHttp = new(always500) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? serverError = await TryExtract(MakeExtractor(alwaysHttp, attempts: 3));
    Assert(always500.Calls == 3 && serverError is HttpRequestException { StatusCode: HttpStatusCode.InternalServerError },
        $"exhausted 5xx still throws (calls={always500.Calls}, {serverError?.GetType().Name}: {serverError?.Message})");
}

ScriptedHandler timedOut = new();
timedOut.EnqueueThrow(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds."));
timedOut.EnqueueOk(emptyEntities);
using (HttpClient timedHttp = new(timedOut) { BaseAddress = new Uri("http://llm.test") })
{
    Exception? timed = await TryExtract(MakeExtractor(timedHttp, attempts: 3));
    Assert(timed is null && timedOut.Calls == 2, $"HttpClient timeout is retried (calls={timedOut.Calls}, {timed?.Message})");
}

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    return 1;
}

Console.WriteLine("ALL PASSED");
return 0;

static string Assistant(string payload) =>
    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(payload) + "}}]}";

static LlmEntityExtractor MakeExtractor(HttpClient http, int attempts, ILogger<LlmEntityExtractor>? log = null) =>
    new(http, new LlmRuntimeConfig
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
        NerMaxAttempts = attempts,
    }, log ?? new QuietLog());

static async Task<Exception?> TryExtract(LlmEntityExtractor extractor)
{
    try
    {
        await extractor.ExtractAsync(
            [new OcrPageResult { Page = 1, Text = "甲方北京华腾科技有限公司" }],
            CancellationToken.None);
        return null;
    }
    catch (Exception ex)
    {
        return ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerExceptions[0] : ex;
    }
}

static ChatCompletionRequest ReadRequest(string body) =>
    JsonSerializer.Deserialize(body, AppJsonContext.Default.ChatCompletionRequest)
    ?? throw new InvalidOperationException("empty chat request");

static bool IsThinkingCorrection(string body)
{
    ChatCompletionRequest request = ReadRequest(body);
    if (request.Thinking?.Type != "enabled")
        return false;
    foreach (ChatMessage message in request.Messages)
    {
        if (message.Role == "user" && (message.Content ?? "").Contains("错误输出", StringComparison.Ordinal))
            return true;
    }

    return false;
}

static string UserText(string body)
{
    ChatCompletionRequest? request = JsonSerializer.Deserialize(body, AppJsonContext.Default.ChatCompletionRequest);
    if (request is null || request.Messages.Count == 0)
        return body;
    return request.Messages[^1].Content ?? body;
}

sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _steps = new();
    private readonly List<string> _requestBodies = [];

    public int Calls { get; private set; }

    public IReadOnlyList<string> RequestBodies => _requestBodies;

    public void EnqueueOk(string json) => EnqueueStatus(HttpStatusCode.OK, json);

    public void EnqueueStatus(HttpStatusCode status, string body)
    {
        _steps.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    public void EnqueueThrow(Exception ex) => _steps.Enqueue(() => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string requestBody = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _requestBodies.Add(requestBody);
        Calls++;
        if (_steps.Count == 0)
            throw new InvalidOperationException($"unexpected LLM call {Calls}");
        return _steps.Dequeue()();
    }
}

sealed class ListLog : ILogger<LlmEntityExtractor>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Lines.Add(formatter(state, exception));
    }
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
