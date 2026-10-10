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

void AssertContains(IEnumerable<EntityHit> hits, string name, string msg)
{
    bool ok = hits.Any(h => h.Name == name);
    AssertTrue(ok, msg + $" (expect '{name}', got: [{string.Join(", ", hits.Select(h => h.Name))}])");
}

Console.WriteLine("=== EntityExtractor smoke ===");

string page1 =
    """
    合同编号：HT-2026-001
    甲方：北京华为技术有限公司
    乙方：上海浦东发展银行股份有限公司
    法定代表人：张伟
    联系人：李娜女士
    英文方：Acme Trading Ltd.
    Signed by: John Smith
    """;

string page2 =
    """
    公司名称：深圳市腾讯计算机系统有限公司
    负责人：王芳
    经办人：欧阳锋
    另见 Microsoft Corporation 与 中国工商银行
    出席：陈晓东经理、赵丽
    """;

string page3 =
    """
    本页无实体，仅有说明文字与日期 2026年9月21日。
    """;

OcrEntities entities = EntityExtractor.ExtractFromPages([page1, page2, page3]);

Console.WriteLine("Companies:");
foreach (EntityHit h in entities.Companies)
    Console.WriteLine($"  - {h.Name}  pages=[{string.Join(",", h.Pages)}] count={h.Count}");

Console.WriteLine("Persons:");
foreach (EntityHit h in entities.Persons)
    Console.WriteLine($"  - {h.Name}  pages=[{string.Join(",", h.Pages)}] count={h.Count}");

AssertContains(entities.Companies, "北京华为技术有限公司", "label 甲方 company");
AssertContains(entities.Companies, "上海浦东发展银行股份有限公司", "suffix 股份有限公司");
AssertContains(entities.Companies, "深圳市腾讯计算机系统有限公司", "label 公司名称");
AssertContains(entities.Companies, "Acme Trading Ltd.", "English Ltd");
AssertContains(entities.Companies, "Microsoft Corporation", "English Corporation");
AssertContains(entities.Companies, "中国工商银行", "suffix 银行");
AssertTrue(!entities.Companies.Any(c => c.Name == "上海浦东发展银行"),
    "shorter 银行 prefix suppressed when 股份有限公司 form exists");

AssertContains(entities.Persons, "张伟", "label 法定代表人");
AssertContains(entities.Persons, "李娜", "title 女士");
AssertContains(entities.Persons, "王芳", "label 负责人");
AssertContains(entities.Persons, "欧阳锋", "compound surname");
AssertContains(entities.Persons, "陈晓东", "title 经理");
AssertContains(entities.Persons, "John Smith", "English person");

// Page refs / dedup
EntityHit? hw = entities.Companies.FirstOrDefault(c => c.Name.Contains("华为", StringComparison.Ordinal));
AssertTrue(hw is not null && hw.Pages.Contains(1), "华为 page ref includes 1");

EntityHit? tw = entities.Companies.FirstOrDefault(c => c.Name.Contains("腾讯", StringComparison.Ordinal));
AssertTrue(tw is not null && tw.Pages.Contains(2), "腾讯 page ref includes 2");

// Should not invent entities on empty-ish page
AssertTrue(!entities.Persons.Any(p => p.Pages.Contains(3) && p.Pages.Count == 1 && p.Count == 1 && p.Name.Length == 2 && page3.Contains(p.Name)),
    "page3 should not be a major person source");

// Org context: person inside company name should be suppressed when possible
OcrEntities orgTrap = EntityExtractor.ExtractFromPages(
[
    "买方为李宁体育用品有限公司，联系人：周杰。",
]);
AssertContains(orgTrap.Companies, "李宁体育用品有限公司", "org trap company");
AssertContains(orgTrap.Persons, "周杰", "org trap real person");
AssertTrue(!orgTrap.Persons.Any(p => p.Name == "李宁"), "should not extract 李宁 from 李宁体育用品有限公司");

Console.WriteLine();
Console.WriteLine("=== ChallengeResultMapper / originText ===");

string otPage =
    "法定代表人或其委托代理人：　游春燕（签字或盖章）正本成都交子商圈物业服务有限公司2026年度一标段（写字楼、";
List<string> originsPerson = ChallengeResultMapper.BuildOriginTexts(otPage, "游春燕");
AssertTrue(originsPerson.Count == 1, "person origin count");
AssertTrue(originsPerson[0].Length is >= 10 and <= 100, $"person origin len={originsPerson[0].Length}");
AssertTrue(originsPerson[0].Contains("游春燕", StringComparison.Ordinal), "person origin contains name");

List<string> originsCo = ChallengeResultMapper.BuildOriginTexts(otPage, "成都交子商圈物业服务有限公司");
AssertTrue(originsCo.Count == 1, "company origin count");
AssertTrue(originsCo[0].Length is >= 10 and <= 100, $"company origin len={originsCo[0].Length}");

ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(
    "f1",
    [new OcrPageResult { Page = 1, Text = otPage }],
    ["成都交子商圈物业服务有限公司"],
    ["游春燕"]);
AssertTrue(mapped.FileId == "f1", "fileId");
AssertTrue(mapped.Pages.Count == 1, "one page");
ChallengeRule? b04 = mapped.Pages[0].RuleList.FirstOrDefault(r => r.RuleCode == "B04");
ChallengeRule? b06 = mapped.Pages[0].RuleList.FirstOrDefault(r => r.RuleCode == "B06");
AssertTrue(b04 is not null && b04.RuleItemList.Count == 1 && b04.RuleItemList[0].PersonName == "游春燕", "B04 personName");
AssertTrue(b04!.RuleItemList[0].Count == 1, "B04 count");
AssertTrue(b06 is not null && b06.RuleItemList.Count == 1 && b06.RuleItemList[0].CompanyName == "成都交子商圈物业服务有限公司", "B06 companyName");

// multi occurrence count
string dup = "张伟出席。再次提到张伟。";
List<string> dupOrigins = ChallengeResultMapper.BuildOriginTexts(dup, "张伟");
AssertTrue(dupOrigins.Count == 2, $"dup origins count={dupOrigins.Count}");

Console.WriteLine();
Console.WriteLine("=== blank pages omitted from protocol output ===");

ChallengeFileResult skipped = ChallengeResultMapper.BuildFileResult(
    "f2",
    [
        new OcrPageResult { Page = 1, Text = otPage },
        new OcrPageResult { Page = 2, Text = "   \n\t" },
        new OcrPageResult { Page = 3, Text = "本合同封面，没有名称。" },
        new OcrPageResult { Page = 4, Text = "联系人：张伟出席会议。" },
    ],
    ["成都交子商圈物业服务有限公司"],
    ["游春燕", "张伟"]);
AssertTrue(skipped.Pages.Count == 2, "blank page and page with no rules omitted");
AssertTrue(skipped.Pages[0].Page == 1 && skipped.Pages[1].Page == 4, "original page numbers kept");
AssertTrue(
    skipped.Pages[1].RuleList.Any(r => r.RuleCode == "B04" && r.RuleItemList.Any(i => i.PersonName == "张伟")),
    "person on page 4 kept");

OcrResponse vision = new()
{
    Pages =
    [
        new OcrPageResult { Page = 1, Text = "" },
        new OcrPageResult
        {
            Page = 2,
            Text = "只有正文，没有名单。",
            RuleList = [],
        },
        new OcrPageResult
        {
            Page = 3,
            Text = "  ",
            RuleList =
            [
                new ChallengeRule
                {
                    RuleCode = "B04",
                    RuleName = "人员名称",
                    RuleItemList =
                    [
                        new ChallengeRuleItem
                        {
                            PersonName = "张伟",
                            Count = 1,
                            OriginText = ["出席人员包括张伟与同事若干人"],
                        },
                    ],
                },
            ],
        },
    ],
};
ChallengeFileResult visionMapped = ChallengeResultMapper.BuildFileResult("v", vision);
AssertTrue(visionMapped.Pages.Count == 1 && visionMapped.Pages[0].Page == 3, "vision rules kept when text blank; empty page dropped");

Console.WriteLine();
Console.WriteLine("=== LLM page groups ===");

List<OcrPageResult> twentyFive = [];
for (int i = 1; i <= 25; i++)
    twentyFive.Add(new OcrPageResult { Page = i, Text = "正文" + i });
List<LlmPageGrouper.PageBatch> groups = LlmPageGrouper.BuildGroups(twentyFive, pagesPerRequest: 10, maxChars: 300_000);
AssertTrue(groups.Count == 3, $"25 pages → 3 groups, got {groups.Count}");
AssertTrue(groups[0].PageNumbers.Length == 10 && groups[0].PageNumbers[0] == 1 && groups[0].PageNumbers[9] == 10, "group1 pages 1-10");
AssertTrue(groups[1].PageNumbers[0] == 11 && groups[1].PageNumbers[9] == 20, "group2 pages 11-20");
AssertTrue(groups[2].PageNumbers.Length == 5 && groups[2].PageNumbers[0] == 21 && groups[2].PageNumbers[4] == 25, "group3 pages 21-25");

List<OcrPageResult> withBlanks = [];
for (int i = 1; i <= 15; i++)
    withBlanks.Add(new OcrPageResult { Page = i, Text = i % 3 == 0 ? "  " : "字" + i });
List<LlmPageGrouper.PageBatch> nonEmptyGroups = LlmPageGrouper.BuildGroups(withBlanks, pagesPerRequest: 10, maxChars: 300_000);
int nonEmptyCount = withBlanks.Count(p => !LlmPageGrouper.IsBlank(p));
AssertTrue(nonEmptyCount == 10, $"expected 10 non-empty, got {nonEmptyCount}");
AssertTrue(nonEmptyGroups.Count == 1 && nonEmptyGroups[0].PageNumbers.Length == 10, "10 non-empty pages → one request");
AssertTrue(!nonEmptyGroups[0].PageNumbers.Any(n => n % 3 == 0), "blank page numbers are not in the group");
AssertTrue(nonEmptyGroups[0].PageNumbers[0] == 1 && nonEmptyGroups[0].PageNumbers[^1] == 14, "original numbers 1..14 skipping multiples of 3");

List<LlmPageGrouper.PageBatch> allBlank = LlmPageGrouper.BuildGroups(
    [new OcrPageResult { Page = 1, Text = "" }, new OcrPageResult { Page = 2, Text = " \n" }],
    10,
    300_000);
AssertTrue(allBlank.Count == 0, "all-blank document makes no LLM group");

OcrPageResult longPage = new() { Page = 7, Text = new string('甲', 80) };
int oneLen = LlmPageGrouper.FormatPage(longPage).Length;
OcrPageResult longPage2 = new() { Page = 8, Text = new string('乙', 80) };
List<LlmPageGrouper.PageBatch> split = LlmPageGrouper.BuildGroups(
    [longPage, longPage2],
    pagesPerRequest: 10,
    maxChars: oneLen);
AssertTrue(split.Count == 2 && split[0].PageNumbers is [7] && split[1].PageNumbers is [8], "char cap splits before 10 pages");

OcrPageResult huge = new() { Page = 2, Text = new string('丙', 200) };
List<LlmPageGrouper.PageBatch> truncated = LlmPageGrouper.BuildGroups([huge], 10, 40);
AssertTrue(truncated.Count == 1 && truncated[0].PageNumbers is [2] && truncated[0].Text.Length == 40, "oversized page truncated and sent alone");

LlmPageGrouper.OrderedBuffer buffer = new(5, pagesPerRequest: 2, maxChars: 100_000);
AssertTrue(buffer.Add(new OcrPageResult { Page = 2, Text = "乙" }).Count == 0, "out-of-order page 2 waits for page 1");
List<LlmPageGrouper.PageBatch> firstReady = buffer.Add(new OcrPageResult { Page = 1, Text = "甲" });
AssertTrue(firstReady.Count == 1 && firstReady[0].PageNumbers is [1, 2], "group emits once the prefix has 2 non-empty pages");
AssertTrue(buffer.Add(new OcrPageResult { Page = 3, Text = "   " }).Count == 0, "blank page emits nothing");
AssertTrue(buffer.Add(new OcrPageResult { Page = 5, Text = "戊" }).Count == 0, "page 5 waits for page 4");
List<LlmPageGrouper.PageBatch> secondReady = buffer.Add(new OcrPageResult { Page = 4, Text = "丁" });
AssertTrue(secondReady.Count == 1 && secondReady[0].PageNumbers is [4, 5], "next group keeps original page numbers");
AssertTrue(buffer.FlushRemainder().Count == 0, "no trailing partial group");
AssertTrue(buffer.NonEmptyPages.Select(p => p.Page).SequenceEqual([1, 2, 4, 5]), "non-empty pages recorded in order");

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine("All smoke checks passed.");
    return 0;
}

Console.WriteLine($"FAILED: {failed} assertion(s).");
return 1;
