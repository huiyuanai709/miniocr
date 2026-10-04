using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

Console.WriteLine("=== post-processor unit checks ===");
RunUnitChecks(AssertTrue);

bool live = args.Any(a => a == "--live");
bool recorded = args.Any(a => a == "--recorded") || !live;
string? saveRecorded = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--save-recorded" && i + 1 < args.Length)
        saveRecorded = args[i + 1];
}

string datasetPath = FindDataFile(Path.Combine("dataset", "samples.json"));
DatasetFile dataset = LoadDataset(datasetPath);
Console.WriteLine($"Dataset: {dataset.Samples.Count} samples ({datasetPath})");

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed} unit check(s) before scoring.");
    return 1;
}

if (live)
{
    string? apiKey = Environment.GetEnvironmentVariable("MINIOCR_LLM_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("LIVE NOT RUN: MINIOCR_LLM_API_KEY is not set.");
        Console.WriteLine("Recorded-mode numbers below are post-processing on simulated responses, not a live model.");
        if (!recorded)
            return 2;
        live = false;
    }
    else
    {
        string baseUrl = Environment.GetEnvironmentVariable("MINIOCR_LLM_BASE_URL") ?? "https://api.deepseek.com";
        string model = Environment.GetEnvironmentVariable("MINIOCR_LLM_MODEL") ?? "deepseek-chat";
        bool thinking = ParseThinking(Environment.GetEnvironmentVariable("MINIOCR_LLM_THINKING"));
        int timeout = 120;
        string? timeoutRaw = Environment.GetEnvironmentVariable("MINIOCR_LLM_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(timeoutRaw) && int.TryParse(timeoutRaw, out int parsedTimeout))
            timeout = Math.Clamp(parsedTimeout, 5, 600);

        Console.WriteLine($"=== live {model} @ {baseUrl.TrimEnd('/')} thinking={(thinking ? "enabled" : "disabled")} ===");
        List<RawResponse> liveRaw = await CallLiveAsync(dataset, baseUrl, apiKey, model, thinking, timeout);
        if (saveRecorded is not null)
            SaveRecorded(saveRecorded, liveRaw);
        ScorePair("live baseline (exact substring, no filters)", dataset, liveRaw, postProcess: false);
        ScoreReport after = ScorePair("live post-process", dataset, liveRaw, postProcess: true);
        if (!Gate(after))
            failed++;
        if (liveRaw.Any(r => r.Error is not null))
        {
            Console.WriteLine("LIVE HAD REQUEST ERRORS.");
            failed++;
        }
    }
}

if (recorded)
{
    string recordedPath = FindDataFile(Path.Combine("recorded", "responses.json"));
    List<RawResponse> raw = LoadRecorded(recordedPath, dataset);
    Console.WriteLine($"Recorded responses: {recordedPath}");
    Console.WriteLine("These responses are simulated. They are not a measurement of the live prompt.");
    ScorePair("recorded baseline (legacy exact match, no filters)", dataset, raw, postProcess: false);
    ScoreReport after = ScorePair("recorded post-process", dataset, raw, postProcess: true);
    if (!Gate(after))
        failed++;
    if (!CheckOrigins(dataset, raw))
        failed++;
}

if (failed > 0)
{
    Console.WriteLine($"FAILED: {failed} check(s).");
    return 1;
}

Console.WriteLine("All entity-eval checks passed.");
return 0;

static void RunUnitChecks(Action<bool, string> assert)
{
    OcrEntities spaced = EntityPostProcessor.Merge(
        [new OcrPageResult { Page = 1, Text = "甲 方：北 京 华 为 技 术 有 限 公 司\n联系人：李娜女士" }],
        ["北京华为技术有限公司", "甲方"],
        ["李娜女士", "并不存在的人"]);
    assert(spaced.Companies.Any(c => c.Name == "北京华为技术有限公司"), "spaced company aligned");
    assert(!spaced.Companies.Any(c => c.Name == "甲方"), "role is not a company");
    assert(spaced.Persons.Any(p => p.Name == "李娜"), "title stripped");
    assert(!spaced.Persons.Any(p => p.Name.Contains("并不存在", StringComparison.Ordinal)), "hallucination dropped");

    List<string> origins = ChallengeResultMapper.BuildOriginTexts(
        "甲 方：北 京 华 为 技 术 有 限 公 司，联系人见附件说明。",
        "北京华为技术有限公司");
    assert(origins.Count == 1, "one origin for spaced company");
    assert(origins[0].Contains("北京华为技术有限公司", StringComparison.Ordinal), "origin contains repaired company");
    assert(origins[0].Length is >= 10 and <= 100, $"origin len={origins[0].Length}");

    OcrEntities masked = EntityPostProcessor.Merge(
        [new OcrPageResult { Page = 1, Text = "原告：张某。被告北京示例科技有限公司。北京市海淀区人民法院。审判长王强。" }],
        ["北京示例科技有限公司", "北京市海淀区人民法院"],
        ["张某", "王强", "原告"]);
    assert(masked.Persons.Select(p => p.Name).SequenceEqual(["王强"]), "masked name and role dropped");
    assert(masked.Companies.Select(c => c.Name).SequenceEqual(["北京示例科技有限公司"]), "court dropped");

    OcrEntities branch = EntityPostProcessor.Merge(
        [new OcrPageResult
        {
            Page = 1,
            Text = "买方：北京星河科技有限公司\n卖方：北京星河科技有限公司深圳分公司\n（中国工商银行）\n中国工商银行股份有限公司",
        }],
        ["北京星河科技有限公司", "北京星河科技有限公司深圳分公司", "中国工商银行", "中国工商银行股份有限公司"],
        []);
    assert(branch.Companies.Any(c => c.Name == "北京星河科技有限公司"), "parent company kept");
    assert(branch.Companies.Any(c => c.Name == "北京星河科技有限公司深圳分公司"), "branch kept");
    assert(!branch.Companies.Any(c => c.Name == "中国工商银行"), "short bank dropped");

    OcrEntities inside = EntityPostProcessor.Merge(
        [new OcrPageResult { Page = 1, Text = "买方为李宁体育用品有限公司，联系人：周杰。" }],
        ["李宁体育用品有限公司"],
        ["李宁", "周杰"]);
    assert(!inside.Persons.Any(p => p.Name == "李宁"), "person inside company dropped");
    assert(inside.Persons.Any(p => p.Name == "周杰"), "standalone person kept");

    OcrPageResult[] split =
    [
        new() { Page = 8, Text = "贷款人：深圳前海微众银行股份" },
        new() { Page = 9, Text = "有限公司。借款人签字：赵敏。" },
    ];
    OcrEntities cross = EntityPostProcessor.Merge(split, ["深圳前海微众银行股份有限公司"], ["赵敏"]);
    assert(cross.Companies.Count == 1 && cross.Companies[0].Pages.Contains(8), "cross-page company kept on the starting page");
    assert(cross.Persons.Count == 1 && cross.Persons[0].Pages.Contains(9), "person stays on the second page");

    string fenced = NerPrompt.ExtractJsonObject("```json\n{\"companies\":[\"甲\"],\"persons\":[]}\n```");
    (List<string> fenceCompanies, _) = ParsePayload(fenced);
    assert(fenceCompanies.Count == 1 && fenceCompanies[0] == "甲", "markdown fence still extracts the object");
    (List<string> trailingCompanies, List<string> trailingPersons) =
        ParsePayload("""{"companies":["甲公司",],"persons":["张伟",]}""");
    assert(trailingCompanies.Count == 1 && trailingCompanies[0] == "甲公司", "trailing comma in companies");
    assert(trailingPersons.Count == 1 && trailingPersons[0] == "张伟", "trailing comma in persons");
    (_, List<string> commented) = ParsePayload("""{ /*keep*/ "companies": [ ], "persons": ["李娜"] }""");
    assert(commented.Count == 1 && commented[0] == "李娜", "json comments are skipped");
    assert(NerPrompt.Text.Contains("json", StringComparison.OrdinalIgnoreCase), "NER prompt mentions json");

    OcrEntities expanded = EntityPostProcessor.Merge(
        [new OcrPageResult { Page = 1, Text = "乙 方：上 海 浦 东 发 展 银 行 股 份 有 限 公 司" }],
        ["上海浦东发展银行"],
        []);
    assert(
        expanded.Companies.Count == 1 && expanded.Companies[0].Name == "上海浦东发展银行股份有限公司",
        "short bank expanded to the legal name in the text");
    ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(
        "x",
        split,
        cross.Companies.Select(c => c.Name).ToList(),
        cross.Persons.Select(p => p.Name).ToList());
    string? snippet = mapped.Pages[0].RuleList
        .SelectMany(r => r.RuleItemList)
        .SelectMany(i => i.OriginText)
        .FirstOrDefault();
    assert(snippet is not null && snippet.Contains("深圳前海微众银行股份有限公司", StringComparison.Ordinal),
        "cross-page origin contains the joined name");
}

static ScoreReport ScorePair(string title, DatasetFile dataset, List<RawResponse> raw, bool postProcess)
{
    int personTp = 0, personFp = 0, personFn = 0;
    int companyTp = 0, companyFp = 0, companyFn = 0;
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
    foreach (Sample sample in dataset.Samples)
    {
        RawResponse response = raw.First(r => r.Id == sample.Id);
        List<OcrPageResult> pages = sample.Pages
            .Select(p => new OcrPageResult { Page = p.Page, Text = p.Text })
            .ToList();
        List<string> persons;
        List<string> companies;
        if (postProcess)
        {
            OcrEntities entities = EntityPostProcessor.Merge(pages, response.Companies, response.Persons);
            persons = entities.Persons.Select(p => p.Name).ToList();
            companies = entities.Companies.Select(c => c.Name).ToList();
        }
        else
        {
            persons = LegacyKeep(pages, response.Persons);
            companies = LegacyKeep(pages, response.Companies);
        }

        (int tp, int fp, int fn) ps = Counts(sample.Persons, persons);
        (int tp, int fp, int fn) cs = Counts(sample.Companies, companies);
        personTp += ps.tp;
        personFp += ps.fp;
        personFn += ps.fn;
        companyTp += cs.tp;
        companyFp += cs.fp;
        companyFn += cs.fn;
        if (ps.fp + ps.fn + cs.fp + cs.fn > 0)
        {
            Console.WriteLine(
                $"  {sample.Id}  persons miss=[{Miss(sample.Persons, persons)}] extra=[{Extra(sample.Persons, persons)}]" +
                $"  companies miss=[{Miss(sample.Companies, companies)}] extra=[{Extra(sample.Companies, companies)}]");
        }
    }

    ScoreReport report = new(personTp, personFp, personFn, companyTp, companyFp, companyFn);
    PrintScore("persons  ", report.PersonTp, report.PersonFp, report.PersonFn);
    PrintScore("companies", report.CompanyTp, report.CompanyFp, report.CompanyFn);
    PrintScore("micro    ", report.MicroTp, report.MicroFp, report.MicroFn);
    return report;
}

static bool CheckOrigins(DatasetFile dataset, List<RawResponse> raw)
{
    bool ok = true;
    foreach (Sample sample in dataset.Samples)
    {
        RawResponse response = raw.First(r => r.Id == sample.Id);
        List<OcrPageResult> pages = sample.Pages
            .Select(p => new OcrPageResult { Page = p.Page, Text = p.Text })
            .ToList();
        OcrEntities entities = EntityPostProcessor.Merge(pages, response.Companies, response.Persons);
        ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(
            sample.Id,
            pages,
            entities.Companies.Select(c => c.Name).ToList(),
            entities.Persons.Select(p => p.Name).ToList());
        HashSet<string> emitted = [];
        foreach (ChallengePageResult page in mapped.Pages)
        {
            foreach (ChallengeRule rule in page.RuleList)
            {
                foreach (ChallengeRuleItem item in rule.RuleItemList)
                {
                    string? name = rule.RuleCode == "B04" ? item.PersonName : item.CompanyName;
                    if (string.IsNullOrEmpty(name))
                    {
                        Console.WriteLine($"  FAIL  {sample.Id} empty name");
                        ok = false;
                        continue;
                    }

                    emitted.Add(name);
                    if (item.Count != item.OriginText.Count || item.Count < 1)
                    {
                        Console.WriteLine($"  FAIL  {sample.Id} {name} count={item.Count} origins={item.OriginText.Count}");
                        ok = false;
                    }

                    foreach (string origin in item.OriginText)
                    {
                        if (origin.Length is < ChallengeResultMapper.OriginMinLen or > ChallengeResultMapper.OriginMaxLen)
                        {
                            Console.WriteLine($"  FAIL  {sample.Id} {name} origin length {origin.Length}");
                            ok = false;
                        }

                        if (!origin.Contains(name, StringComparison.Ordinal))
                        {
                            Console.WriteLine($"  FAIL  {sample.Id} origin missing [{name}]");
                            ok = false;
                        }
                    }
                }
            }
        }

        foreach (string name in entities.Persons.Select(p => p.Name).Concat(entities.Companies.Select(c => c.Name)))
        {
            if (!emitted.Contains(name))
            {
                Console.WriteLine($"  FAIL  {sample.Id} entity [{name}] missing from protocol output");
                ok = false;
            }
        }
    }

    Console.WriteLine(ok ? "  PASS  originText contains the repaired name and is 10–100 chars" : "  FAIL  originText protocol");
    return ok;
}

static bool Gate(ScoreReport report)
{
    bool ok = report.PersonF1 >= 0.90 && report.CompanyF1 >= 0.90 && report.MicroF1 >= 0.90;
    Console.WriteLine(ok
        ? "  PASS  precision/recall/F1 >= 0.90 for persons, companies, and micro"
        : "  FAIL  precision/recall/F1 below 0.90");
    return ok;
}

static (int tp, int fp, int fn) Counts(IReadOnlyList<string> gold, IReadOnlyList<string> pred)
{
    HashSet<string> g = GoldSet(gold);
    HashSet<string> p = GoldSet(pred);
    int tp = p.Count(x => g.Contains(x));
    return (tp, p.Count - tp, g.Count - tp);
}

static HashSet<string> GoldSet(IReadOnlyList<string> names)
{
    HashSet<string> set = new(StringComparer.Ordinal);
    foreach (string name in names)
    {
        if (!string.IsNullOrWhiteSpace(name))
            set.Add(name);
    }

    return set;
}

static string Miss(IReadOnlyList<string> gold, IReadOnlyList<string> pred)
{
    HashSet<string> p = GoldSet(pred);
    return string.Join(", ", gold.Where(g => !p.Contains(g)));
}

static string Extra(IReadOnlyList<string> gold, IReadOnlyList<string> pred)
{
    HashSet<string> g = GoldSet(gold);
    return string.Join(", ", pred.Where(x => !g.Contains(x)));
}

static List<string> LegacyKeep(List<OcrPageResult> pages, IReadOnlyList<string> raw)
{
    List<string> kept = [];
    HashSet<string> seen = new(StringComparer.Ordinal);
    foreach (string item in raw)
    {
        string name = LegacyNorm(item);
        if (name.Length == 0 || !seen.Add(name))
            continue;
        bool found = false;
        foreach (OcrPageResult page in pages)
        {
            if ((page.Text ?? "").Contains(name, StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        if (found)
            kept.Add(name);
    }

    return kept;
}

static bool ParseThinking(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return false;
    string s = raw.Trim();
    return s is "1" or "true" or "True" or "TRUE" or "yes" or "YES" or "on" or "ON"
        or "enabled" or "Enabled" or "ENABLED";
}

static string LegacyNorm(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
        return "";
    StringBuilder sb = new();
    bool space = false;
    foreach (char c in raw.Trim())
    {
        if (c is ' ' or '\t' or '\u3000')
        {
            space = true;
            continue;
        }

        if (space && sb.Length > 0)
            sb.Append(' ');
        space = false;
        sb.Append(c);
    }

    return sb.ToString();
}

static void PrintScore(string label, int tp, int fp, int fn)
{
    double p = tp + fp == 0 ? 1 : (double)tp / (tp + fp);
    double r = tp + fn == 0 ? 1 : (double)tp / (tp + fn);
    double f1 = p + r == 0 ? 0 : 2 * p * r / (p + r);
    Console.WriteLine(
        $"  {label}  P={p:0.000}  R={r:0.000}  F1={f1:0.000}  tp={tp} fp={fp} fn={fn}");
}

static async Task<List<RawResponse>> CallLiveAsync(
    DatasetFile dataset,
    string baseUrl,
    string apiKey,
    string model,
    bool thinking,
    int timeoutSeconds)
{
    using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    string url = baseUrl.TrimEnd('/') + "/v1/chat/completions";
    List<RawResponse> results = [];
    foreach (Sample sample in dataset.Samples)
    {
        string user = string.Concat(sample.Pages.Select(p =>
            LlmPageGrouper.FormatPage(new OcrPageResult { Page = p.Page, Text = p.Text })));
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["thinking"] = new Dictionary<string, string> { ["type"] = thinking ? "enabled" : "disabled" },
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = NerPrompt.Text },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = user },
            },
        };
        try
        {
            using HttpResponseMessage response = await http.PostAsync(
                url,
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            string text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                string snippet = text.Length > 240 ? text[..240] : text;
                Console.WriteLine($"  ERROR {sample.Id} HTTP {(int)response.StatusCode}: {snippet}");
                results.Add(new RawResponse(sample.Id, [], [], $"HTTP {(int)response.StatusCode}"));
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(text);
            string content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            (List<string> companies, List<string> persons) = ParsePayload(content);
            Console.WriteLine($"  {sample.Id}  companies={companies.Count} persons={persons.Count}");
            results.Add(new RawResponse(sample.Id, companies, persons, null));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR {sample.Id} {ex.Message}");
            results.Add(new RawResponse(sample.Id, [], [], ex.Message));
        }
    }

    return results;
}

static (List<string> Companies, List<string> Persons) ParsePayload(string content)
{
    string json = NerPrompt.ExtractJsonObject(content);
    using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    });
    return (ReadArray(doc.RootElement, "companies"), ReadArray(doc.RootElement, "persons"));
}

static List<string> ReadArray(JsonElement root, string name)
{
    List<string> list = [];
    if (!root.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        return list;
    foreach (JsonElement item in arr.EnumerateArray())
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            string? s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
    }

    return list;
}

static void SaveRecorded(string path, List<RawResponse> raw)
{
    var payload = new
    {
        samples = raw.Select(r => new { id = r.Id, companies = r.Companies, persons = r.Persons, error = r.Error }),
    };
    File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Saved recorded responses to {path}");
}

static DatasetFile LoadDataset(string path)
{
    DatasetFile? file = JsonSerializer.Deserialize<DatasetFile>(File.ReadAllText(path), EvalJson.Options);
    if (file is null || file.Samples.Count == 0)
        throw new InvalidOperationException("Dataset is empty: " + path);
    return file;
}

static List<RawResponse> LoadRecorded(string path, DatasetFile dataset)
{
    RecordedFile? file = JsonSerializer.Deserialize<RecordedFile>(File.ReadAllText(path), EvalJson.Options);
    if (file is null)
        throw new InvalidOperationException("Recorded file is empty: " + path);
    List<RawResponse> list = [];
    foreach (Sample sample in dataset.Samples)
    {
        RecordedSample? found = file.Samples.FirstOrDefault(s => s.Id == sample.Id);
        if (found is null)
            throw new InvalidOperationException("Recorded responses missing id " + sample.Id);
        list.Add(new RawResponse(sample.Id, found.Companies ?? [], found.Persons ?? [], null));
    }

    return list;
}

static string FindDataFile(string relative)
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate))
            return candidate;
        dir = dir.Parent;
    }

    throw new FileNotFoundException("Could not find " + relative);
}

static class EvalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

sealed class DatasetFile
{
    public List<Sample> Samples { get; set; } = [];
}

sealed class Sample
{
    public string Id { get; set; } = "";
    public List<PageSpec> Pages { get; set; } = [];
    public List<string> Persons { get; set; } = [];
    public List<string> Companies { get; set; } = [];
}

sealed class PageSpec
{
    public int Page { get; set; }
    public string Text { get; set; } = "";
}

sealed class RecordedFile
{
    public List<RecordedSample> Samples { get; set; } = [];
}

sealed class RecordedSample
{
    public string Id { get; set; } = "";
    public List<string>? Persons { get; set; }
    public List<string>? Companies { get; set; }
}

sealed record RawResponse(string Id, List<string> Companies, List<string> Persons, string? Error);

sealed record ScoreReport(int PersonTp, int PersonFp, int PersonFn, int CompanyTp, int CompanyFp, int CompanyFn)
{
    public int MicroTp => PersonTp + CompanyTp;
    public int MicroFp => PersonFp + CompanyFp;
    public int MicroFn => PersonFn + CompanyFn;
    public double PersonF1 => F1(PersonTp, PersonFp, PersonFn);
    public double CompanyF1 => F1(CompanyTp, CompanyFp, CompanyFn);
    public double MicroF1 => F1(MicroTp, MicroFp, MicroFn);

    private static double F1(int tp, int fp, int fn)
    {
        double p = tp + fp == 0 ? 1 : (double)tp / (tp + fp);
        double r = tp + fn == 0 ? 1 : (double)tp / (tp + fn);
        return p + r == 0 ? 0 : 2 * p * r / (p + r);
    }
}
