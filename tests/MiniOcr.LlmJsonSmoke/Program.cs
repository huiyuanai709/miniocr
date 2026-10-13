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
Assert(redOmitted?.Ocr?.SealOcr == false, "omitted sealOcr stays false");
AppConfigFile? sealOn = JsonSerializer.Deserialize(
    """{"ocr":{"sealOcr":true}}""", AppJsonContext.Default.AppConfigFile);
Assert(sealOn?.Ocr?.SealOcr == true && sealOn.Ocr.RemoveRedSeal, "json sealOcr true keeps removeRedSeal default");

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
    ChatCompletionRequest sent = ReadRequest(distributed.RequestBodies[0]);
    string system = sent.Messages.Find(m => m.Role == "system")?.Content ?? "";
    Assert(system.Contains("{\"p\":", StringComparison.Ordinal), "pages prompt asks for compact JSON");
    Assert(system.Contains("不要输出 originText", StringComparison.Ordinal), "pages prompt does not ask the model to write snippets");
}

const string CompactPayload =
    """{"p":[{"n":1,"c":[["成都交子商圈物业服务有限公司","2"],"人才公司"],"r":[{"n":"张伟","c":1},["李娜"]]},{"n":2,"c":[],"r":[]}]}""";
ScriptedHandler compact = new();
compact.EnqueueOk(Assistant(CompactPayload));
using (HttpClient compactHttp = new(compact) { BaseAddress = new Uri("http://llm.test") })
{
    LlmEntityExtractor compactExtractor = MakeExtractor(compactHttp, attempts: 1);
    string page1 = "正本成都交子商圈物业服务有限公司2026年度合同由张伟签署，人才公司盖章，联系人李娜到场。";
    LlmExtractionResult compactResult = await compactExtractor.ExtractAsync(
        [new OcrPageResult { Page = 1, Text = page1 }],
        CancellationToken.None);
    Assert(compactResult.Pages.Count == 1 && compactResult.Pages[0].Page == 1, "compact reply keeps the page and drops an empty page");
    ChallengeRuleItem? company = null;
    ChallengeRuleItem? person = null;
    foreach (ChallengeRule rule in compactResult.Pages[0].RuleList)
    {
        foreach (ChallengeRuleItem item in rule.RuleItemList)
        {
            if (item.CompanyName == "成都交子商圈物业服务有限公司")
                company = item;
            if (item.PersonName == "张伟")
                person = item;
        }
    }

    Assert(company is not null && company.Count == 2, "compact company count is the model's, not a recount");
    Assert(person is not null && person.Count == 1, "object-shaped person hit keeps its count");
    Assert(compactResult.Pages[0].RuleList.SelectMany(r => r.RuleItemList).Any(i => i.CompanyName == "人才公司"),
        "a bare company name is accepted");
    Assert(compactResult.Pages[0].RuleList.SelectMany(r => r.RuleItemList).Any(i => i.PersonName == "李娜"),
        "a bare person name defaults count to 1");
    Assert(company!.OriginText.Count > 0
        && company.OriginText.All(t => t.Length is >= 10 and <= 100)
        && company.OriginText.All(t => t.Contains("成都交子商圈物业服务有限公司", StringComparison.Ordinal)),
        "originText is rebuilt locally and contains the company name");
    Assert(person!.OriginText.Count > 0 && person.OriginText.All(t => t.Contains("张伟", StringComparison.Ordinal)),
        "originText is rebuilt locally around the person name");
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
    Assert(unauthorized.Authorizations.Count == 1 && unauthorized.Authorizations[0] == "Bearer test-key",
        "single key is still sent as a Bearer token");
    Assert(auth?.Message?.Contains("test-key", StringComparison.Ordinal) != true
        && !authLog.Lines.Exists(l => l.Contains("test-key", StringComparison.Ordinal)),
        "401 failure does not log the api key");
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

Console.WriteLine("multi-key pool");
string? prevApiKey = Environment.GetEnvironmentVariable("MINIOCR_LLM_API_KEY");
string? prevApiKeys = Environment.GetEnvironmentVariable("MINIOCR_LLM_API_KEYS");
string? prevConcurrency = Environment.GetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY");
string? prevPerKey = Environment.GetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY_PER_KEY");
try
{
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEY", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEYS", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY_PER_KEY", null);

    AppConfigFile? keyFile = JsonSerializer.Deserialize(
        """{"llm":{"apiKey":"file-one","apiKeys":["k2","k1"],"maxConcurrencyPerKey":6}}""",
        AppJsonContext.Default.AppConfigFile);
    Assert(keyFile?.Llm?.ApiKeys is ["k2", "k1"] && keyFile.Llm.MaxConcurrencyPerKey == 6,
        "config json reads apiKeys and maxConcurrencyPerKey");

    LlmRuntimeConfig single = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { ApiKey = "only-key", MaxConcurrency = 4 },
    });
    Assert(single.ApiKey == "only-key" && single.ApiKeyCount == 1 && single.ApiKeys.Count == 1,
        "single apiKey still resolves to a one-key pool");
    Assert(single.MaxConcurrencyPerKey == 4 && single.MaxConcurrency == 4,
        "one key keeps maxConcurrency as the effective NER limit");
    Assert(!single.IsUsable || single.ApiKey == "only-key", "single-key config stays usable when enabled");
    Assert(single.IsUsable, "enabled + apiKey + default baseUrl and model is usable");

    LlmRuntimeConfig merged = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig
        {
            ApiKey = " k1 ",
            ApiKeys = ["", "  ", "k2", "k1", "k2"],
            MaxConcurrency = 4,
        },
    });
    Assert(merged.ApiKeys.Count == 2 && merged.ApiKeys[0] == "k2" && merged.ApiKeys[1] == "k1",
        "file apiKeys and apiKey are trimmed, merged, and de-duplicated");
    Assert(merged.ApiKey == "k2", "ApiKey is the first pooled key and is not logged by resolve");
    Assert(merged.MaxConcurrencyPerKey == 4 && merged.MaxConcurrency == 8,
        "unset maxConcurrencyPerKey reuses maxConcurrency per key, so two keys allow 8 in flight");

    LlmRuntimeConfig preferred = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig
        {
            ApiKeys = ["k1", "k2"],
            MaxConcurrency = 4,
            MaxConcurrencyPerKey = 6,
        },
    });
    Assert(preferred.MaxConcurrencyPerKey == 6 && preferred.MaxConcurrency == 12,
        "maxConcurrencyPerKey wins over maxConcurrency");

    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEYS", "k3;k4\nk3");
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEY", "k1");
    LlmRuntimeConfig fromEnv = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig
        {
            ApiKey = "ignored-file",
            ApiKeys = ["ignored"],
            MaxConcurrency = 9,
            MaxConcurrencyPerKey = 3,
        },
    });
    Assert(fromEnv.ApiKeys.Count == 3
        && fromEnv.ApiKeys[0] == "k3"
        && fromEnv.ApiKeys[1] == "k4"
        && fromEnv.ApiKeys[2] == "k1",
        "MINIOCR_LLM_API_KEYS replaces the file list; MINIOCR_LLM_API_KEY is merged and de-duplicated");
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEYS", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEY", "env-one");
    LlmRuntimeConfig envSingle = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { ApiKey = "file-one" },
    });
    Assert(envSingle.ApiKey == "env-one" && envSingle.ApiKeyCount == 1,
        "MINIOCR_LLM_API_KEY still overrides the file apiKey");
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEY", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY", "9");
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY_PER_KEY", "7");
    LlmRuntimeConfig perKeyEnv = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig
        {
            ApiKeys = ["k1", "k2"],
            MaxConcurrency = 4,
            MaxConcurrencyPerKey = 3,
        },
    });
    Assert(perKeyEnv.MaxConcurrencyPerKey == 7 && perKeyEnv.MaxConcurrency == 14,
        "MINIOCR_LLM_MAX_CONCURRENCY_PER_KEY overrides the file and maxConcurrency");

    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEY", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEYS", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY", null);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY_PER_KEY", null);
    LlmRuntimeConfig defaults = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { ApiKeys = ["a", "b"] },
    });
    Assert(defaults.MaxConcurrencyPerKey == 8 && defaults.MaxConcurrency == 16,
        "unset per-key concurrency stays 8, so two keys allow 16 in flight");

    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY", "100");
    LlmRuntimeConfig scaled = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { ApiKeys = ["a", "b"] },
    });
    Assert(scaled.MaxConcurrencyPerKey == 100 && scaled.MaxConcurrency == 200,
        "per-key concurrency is not clamped at 32; the effective limit is keys times that");

    var manyKeys = new List<string>(40);
    for (int i = 0; i < 40; i++)
        manyKeys.Add("key-" + i.ToString());
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY", "80");
    LlmRuntimeConfig wide = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { ApiKeys = manyKeys },
    });
    Assert(wide.ApiKeyCount == 40 && wide.MaxConcurrencyPerKey == 80 && wide.MaxConcurrency == 3200,
        "total NER in-flight is N×K and is not capped at 32×64");
    LlmKeyPool widePool = LlmKeyPool.FromConfig(wide);
    Assert(widePool.KeyCount == 40 && widePool.PerKey == 80 && widePool.MaxInFlight == 3200,
        "the key pool exposes the same N×K slots with no global ceiling");

    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY", "0");
    LlmRuntimeConfig floored = AppConfigStore.ResolveLlm(new AppConfigFile
    {
        Llm = new LlmFileConfig { ApiKey = "only" },
    });
    Assert(floored.MaxConcurrencyPerKey == 1 && floored.MaxConcurrency == 1,
        "a non-positive concurrency is raised to 1");

    LlmKeyPool reused = LlmKeyPool.FromConfig(new LlmRuntimeConfig
    {
        ApiKeys = ["a", "b"],
        MaxConcurrency = 1,
    });
    Assert(reused.PerKey == 1 && reused.MaxInFlight == 2 && reused.KeyCount == 2,
        "without maxConcurrencyPerKey, maxConcurrency is the per-key slot count");
    LlmKeyPool explicitPool = LlmKeyPool.FromConfig(new LlmRuntimeConfig
    {
        ApiKey = "a",
        ApiKeys = ["a", "b"],
        MaxConcurrency = 1,
        MaxConcurrencyPerKey = 4,
    });
    Assert(explicitPool.KeyCount == 2 && explicitPool.PerKey == 4 && explicitPool.MaxInFlight == 8,
        "maxConcurrencyPerKey is the slot count even when maxConcurrency is smaller");
    LlmKeyPool redactor = new(["sk-live-secret-aaa", "sk-live-secret-aa"], 1);
    Assert(redactor.Redact("bad sk-live-secret-aaa") == "bad ***", "redact removes a key and prefers the longer match");

    LlmKeyPool slots = new(["k1", "k2"], 1);
    LlmKeyPool.LlmKeyLease first = await slots.AcquireAsync(CancellationToken.None);
    LlmKeyPool.LlmKeyLease second = await slots.AcquireAsync(CancellationToken.None);
    Assert(first.Slot == 0 && first.Key == "k1" && second.Slot == 1 && second.Key == "k2",
        "idle keys are handed out round-robin");
    using CancellationTokenSource blocked = new(TimeSpan.FromMilliseconds(200));
    bool waited = false;
    try
    {
        await slots.AcquireAsync(blocked.Token);
    }
    catch (OperationCanceledException)
    {
        waited = true;
    }

    Assert(waited, "a third acquire waits while both per-key slots are taken");
    first.Dispose();
    LlmKeyPool.LlmKeyLease reusedSlot = await slots.AcquireAsync(CancellationToken.None);
    Assert(reusedSlot.Key == "k1", "a freed slot is preferred over a busier key");
    Assert(slots.Disable(second.Slot) && !slots.Disable(second.Slot), "disable is sticky for the process");
    reusedSlot.Dispose();
    second.Dispose();
    LlmKeyPool.LlmKeyLease afterDisable = await slots.AcquireAsync(CancellationToken.None);
    Assert(afterDisable.Key == "k1" && slots.LiveCount == 1, "a disabled key is not issued again");
    afterDisable.Dispose();
    Assert(first.ToString() == "LlmKeyLease(slot=0)", "lease text is the slot index and not the key");
}
finally
{
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEY", prevApiKey);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_API_KEYS", prevApiKeys);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY", prevConcurrency);
    Environment.SetEnvironmentVariable("MINIOCR_LLM_MAX_CONCURRENCY_PER_KEY", prevPerKey);
}

const string SecretA = "sk-live-secret-aaa";
const string SecretB = "sk-live-secret-bbb";
ListLog failoverLog = new();
KeyFailoverHandler failover = new() { Reject = [SecretA] };
using (HttpClient failoverHttp = new(failover) { BaseAddress = new Uri("http://llm.test") })
{
    LlmKeyPool pool = new([SecretA, SecretB], perKey: 2);
    LlmEntityExtractor failoverExtractor = new(failoverHttp, new LlmRuntimeConfig
    {
        Enabled = true,
        BaseUrl = "http://llm.test",
        ApiKey = SecretA,
        ApiKeys = [SecretA, SecretB],
        Model = "test-model",
        TimeoutSeconds = 30,
        PagesPerRequest = 1,
        MaxCharsPerRequest = 100_000,
        MaxConcurrency = 2,
        MaxConcurrencyPerKey = 2,
        JsonObject = true,
        NerMaxAttempts = 3,
    }, failoverLog, pool);
    Exception? failoverError = await TryExtract(failoverExtractor);
    Assert(failoverError is null && failover.Calls == 2, $"401 on one key retries another (calls={failover.Calls}, {failoverError?.Message})");
    Assert(failover.Authorizations.Count == 2
        && failover.Authorizations[0] == "Bearer " + SecretA
        && failover.Authorizations[1] == "Bearer " + SecretB,
        "the retry uses the next live key");
    Assert(pool.LiveCount == 1, "the rejected key stays disabled");
    string logs = string.Join("\n", failoverLog.Lines);
    Assert(!logs.Contains(SecretA, StringComparison.Ordinal) && !logs.Contains(SecretB, StringComparison.Ordinal),
        "NER failover logs do not contain key material");
}

ListLog exhaustedLog = new();
KeyFailoverHandler exhausted = new() { RejectAll = true };
using (HttpClient exhaustedHttp = new(exhausted) { BaseAddress = new Uri("http://llm.test") })
{
    LlmEntityExtractor exhaustedExtractor = new(exhaustedHttp, new LlmRuntimeConfig
    {
        Enabled = true,
        BaseUrl = "http://llm.test",
        ApiKeys = [SecretA, SecretB],
        Model = "test-model",
        TimeoutSeconds = 30,
        PagesPerRequest = 1,
        MaxCharsPerRequest = 100_000,
        MaxConcurrencyPerKey = 1,
        JsonObject = true,
        NerMaxAttempts = 3,
    }, exhaustedLog);
    Exception? exhaustedError = await TryExtract(exhaustedExtractor);
    Assert(exhausted.Calls == 2 && exhaustedError is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized },
        $"401 on every key fails the attempt once (calls={exhausted.Calls}, {exhaustedError?.Message})");
    string message = exhaustedError?.ToString() ?? "";
    string logs = string.Join("\n", exhaustedLog.Lines);
    Assert(!message.Contains(SecretA, StringComparison.Ordinal)
        && !message.Contains(SecretB, StringComparison.Ordinal)
        && !logs.Contains(SecretA, StringComparison.Ordinal)
        && !logs.Contains(SecretB, StringComparison.Ordinal),
        "exhausted 401 does not echo the key from the response body");
}

ListLog overlapLog = new();
OverlapHandler overlap = new();
using (HttpClient overlapHttp = new(overlap) { BaseAddress = new Uri("http://llm.test") })
{
    LlmEntityExtractor overlapExtractor = new(overlapHttp, new LlmRuntimeConfig
    {
        Enabled = true,
        BaseUrl = "http://llm.test",
        ApiKeys = ["k-one", "k-two"],
        Model = "test-model",
        TimeoutSeconds = 30,
        PagesPerRequest = 1,
        MaxCharsPerRequest = 100_000,
        MaxConcurrency = 1,
        MaxConcurrencyPerKey = 1,
        JsonObject = true,
        NerMaxAttempts = 1,
    }, overlapLog);
    using CancellationTokenSource overlapCancel = new(TimeSpan.FromSeconds(5));
    Exception? overlapError = null;
    try
    {
        await overlapExtractor.ExtractAsync(
            [
                new OcrPageResult { Page = 1, Text = "甲方北京华腾科技有限公司" },
                new OcrPageResult { Page = 2, Text = "法定代表人张伟签署本合同" },
            ],
            overlapCancel.Token);
    }
    catch (Exception ex)
    {
        overlapError = ex;
    }

    Assert(overlapError is null && overlap.Peak >= 2, $"two keys run two NER calls together (peak={overlap.Peak}, {overlapError?.Message})");
    Assert(overlap.Authorizations.Contains("Bearer k-one") && overlap.Authorizations.Contains("Bearer k-two"),
        "overlapping calls use different keys");
    string logs = string.Join("\n", overlapLog.Lines);
    Assert(!logs.Contains("k-one", StringComparison.Ordinal) && !logs.Contains("k-two", StringComparison.Ordinal),
        "overlap logs do not contain key material");
}

OverlapHandler wideOverlap = new(releaseAt: 40);
using (HttpClient wideHttp = new(wideOverlap) { BaseAddress = new Uri("http://llm.test") })
{
    var widePages = new OcrPageResult[40];
    for (int i = 0; i < widePages.Length; i++)
        widePages[i] = new OcrPageResult { Page = i + 1, Text = "甲方北京华腾科技有限公司" };
    LlmEntityExtractor wideExtractor = new(wideHttp, new LlmRuntimeConfig
    {
        Enabled = true,
        BaseUrl = "http://llm.test",
        ApiKeys = ["k-one", "k-two"],
        Model = "test-model",
        TimeoutSeconds = 30,
        PagesPerRequest = 1,
        MaxCharsPerRequest = 100_000,
        MaxConcurrency = 20,
        MaxConcurrencyPerKey = 20,
        JsonObject = true,
        NerMaxAttempts = 1,
    }, new QuietLog());
    Exception? wideError = null;
    try
    {
        await wideExtractor.ExtractAsync(widePages, CancellationToken.None);
    }
    catch (Exception ex)
    {
        wideError = ex;
    }

    Assert(wideError is null && wideOverlap.Peak >= 40,
        $"two keys at 20 each run 40 NER calls together (peak={wideOverlap.Peak}, {wideError?.Message})");
}

ListLog visionLog = new();
KeyFailoverHandler visionFailover = new() { Reject = [SecretA] };
using (HttpClient visionHttp = new(visionFailover) { BaseAddress = new Uri("http://llm.test") })
{
    LlmKeyPool visionPool = new([SecretA, SecretB], perKey: 1);
    LlmVisionOcr vision = new(visionHttp, new LlmRuntimeConfig
    {
        Enabled = true,
        BaseUrl = "http://llm.test",
        ApiKeys = [SecretA, SecretB],
        Model = "test-model",
        TimeoutSeconds = 30,
        JsonObject = true,
    }, new QuietVisionLog(), visionPool);
    OcrPageResult page = await vision.RecognizePageAsync(1, 8, 8, [1, 2, 3], 0, CancellationToken.None);
    Assert(page.Text == "hello" && visionFailover.Calls == 2, "vision OCR retries a 401 on the next key");
    Assert(visionFailover.Authorizations.Count == 2 && visionFailover.Authorizations[1] == "Bearer " + SecretB,
        "vision OCR sends the failover key");
    Assert(visionPool.LiveCount == 1, "vision 401 disables the key for the shared pool");
    Exception? nerAfter = await TryExtract(new LlmEntityExtractor(
        new HttpClient(visionFailover, disposeHandler: false) { BaseAddress = new Uri("http://llm.test") },
        new LlmRuntimeConfig
        {
            Enabled = true,
            BaseUrl = "http://llm.test",
            ApiKeys = [SecretA, SecretB],
            Model = "test-model",
            TimeoutSeconds = 30,
            PagesPerRequest = 1,
            MaxConcurrencyPerKey = 1,
            JsonObject = true,
            NerMaxAttempts = 1,
        },
        visionLog,
        visionPool));
    Assert(nerAfter is null && visionFailover.Authorizations[^1] == "Bearer " + SecretB,
        "NER uses the pool after vision disables the bad key");
    string logs = string.Join("\n", visionLog.Lines);
    Assert(!logs.Contains(SecretA, StringComparison.Ordinal) && !logs.Contains(SecretB, StringComparison.Ordinal),
        "shared-pool NER logs do not contain key material");
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

    public List<string> Authorizations { get; } = [];

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
        Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
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

sealed class QuietVisionLog : ILogger<LlmVisionOcr>
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

sealed class KeyFailoverHandler : HttpMessageHandler
{
    public bool RejectAll { get; init; }
    public List<string> Reject { get; init; } = [];
    public int Calls { get; private set; }
    public List<string> Authorizations { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        string auth = request.Headers.Authorization?.ToString() ?? "";
        Authorizations.Add(auth);
        string token = request.Headers.Authorization?.Parameter ?? "";
        bool reject = RejectAll || Reject.Contains(token);
        if (reject)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"bad " + token + "\"}", Encoding.UTF8, "application/json"),
            });
        }

        string payload = """{"text":"hello","companies":[],"persons":[]}""";
        string json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(payload) + "}}]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}

sealed class OverlapHandler : HttpMessageHandler
{
    private readonly int _releaseAt;
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;
    public int Peak { get; private set; }
    public List<string> Authorizations { get; } = [];

    public OverlapHandler(int releaseAt = 2) => _releaseAt = releaseAt;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Authorizations)
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
        int now = Interlocked.Increment(ref _inFlight);
        lock (this)
        {
            if (now > Peak)
                Peak = now;
        }

        if (now >= _releaseAt)
            _gate.TrySetResult();
        try
        {
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        string payload = """{"companies":[],"persons":[]}""";
        string json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(payload) + "}}]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
