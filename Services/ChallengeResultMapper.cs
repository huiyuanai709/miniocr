using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Maps OCR page texts + entity names into competition per-page ruleList,
/// with count = occurrences on that page and originText snippets (10–100 chars).
/// </summary>
public static class ChallengeResultMapper
{
    public const int OriginMinLen = 10;
    public const int OriginMaxLen = 100;

    /// <summary>
    /// True when the page has text or a prebuilt rule list.
    /// Pipeline page lists use this so NER can still search text that produced no names.
    /// External responses do not: <see cref="BuildFileResult(string, OcrResponse)"/> and
    /// verbose debug keep a page only when it has at least one rule with items.
    /// A vision page that returned rules but left <c>text</c> blank still counts.
    /// </summary>
    public static bool IncludeInOutput(OcrPageResult page) =>
        !string.IsNullOrWhiteSpace(page.Text) || page.RuleList is { Count: > 0 };

    /// <summary>
    /// Rules that carry at least one item. An empty rule object is not a hit.
    /// </summary>
    public static List<ChallengeRule> RulesWithItems(IReadOnlyList<ChallengeRule>? rules)
    {
        List<ChallengeRule> kept = [];
        if (rules is null)
            return kept;
        foreach (ChallengeRule rule in rules)
        {
            if (rule.RuleItemList is { Count: > 0 })
                kept.Add(rule);
        }

        return kept;
    }

    public static ChallengeFileResult BuildFileResult(string fileId, OcrResponse ocr)
    {
        // Vision OCR may already attach contest-shaped ruleList per page.
        if (ocr.Pages.Any(p => p.RuleList is { Count: > 0 }))
        {
            List<ChallengePageResult> pages = new(ocr.Pages.Count);
            foreach (OcrPageResult page in ocr.Pages)
            {
                List<ChallengeRule> rules = RulesWithItems(page.RuleList);
                if (rules.Count == 0)
                    continue;
                pages.Add(new ChallengePageResult
                {
                    Page = page.Page,
                    RuleList = rules,
                });
            }

            return new ChallengeFileResult { FileId = fileId, Pages = pages };
        }

        OcrEntities entities = ocr.Entities ?? new OcrEntities();
        List<string> companies = entities.Companies.Select(c => c.Name).Where(n => n.Length > 0).ToList();
        List<string> persons = entities.Persons.Select(p => p.Name).Where(n => n.Length > 0).ToList();
        return BuildFileResult(fileId, ocr.Pages, companies, persons);
    }

    public static ChallengeFileResult BuildFileResult(
        string fileId,
        IReadOnlyList<OcrPageResult> pages,
        IReadOnlyList<string> companyNames,
        IReadOnlyList<string> personNames)
    {
        List<ChallengePageResult> pageResults = new(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            OcrPageResult page = pages[i];
            if (!IncludeInOutput(page))
                continue;
            string? lookahead = EntityText.Lookahead(pages, i);
            string text = page.Text ?? "";
            List<ChallengeRuleItem> personItems = BuildPersonItems(text, lookahead, personNames, companyNames);
            List<ChallengeRuleItem> companyItems = BuildCompanyItems(text, lookahead, companyNames);

            List<ChallengeRule> rules = [];
            if (personItems.Count > 0)
            {
                rules.Add(new ChallengeRule
                {
                    RuleCode = "B04",
                    RuleName = "人员名称",
                    RuleItemList = personItems,
                });
            }

            if (companyItems.Count > 0)
            {
                rules.Add(new ChallengeRule
                {
                    RuleCode = "B06",
                    RuleName = "公司名称",
                    RuleItemList = companyItems,
                });
            }

            if (rules.Count == 0)
                continue;

            pageResults.Add(new ChallengePageResult
            {
                Page = page.Page,
                RuleList = rules,
            });
        }

        return new ChallengeFileResult
        {
            FileId = fileId,
            Pages = pageResults,
        };
    }

    private static List<ChallengeRuleItem> BuildPersonItems(
        string text,
        string? lookahead,
        IReadOnlyList<string> names,
        IReadOnlyList<string> companies)
    {
        List<ChallengeRuleItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;
            List<string> covers = new(companies.Count + names.Count);
            covers.AddRange(companies);
            foreach (string other in names)
            {
                if (other.Length > name.Length)
                    covers.Add(other);
            }

            List<string> origins = BuildOriginTexts(text, name, lookahead, covers);
            if (origins.Count == 0)
                continue;
            items.Add(new ChallengeRuleItem
            {
                PersonName = name,
                Count = origins.Count,
                OriginText = origins,
            });
        }

        return items;
    }

    private static List<ChallengeRuleItem> BuildCompanyItems(
        string text,
        string? lookahead,
        IReadOnlyList<string> names)
    {
        List<ChallengeRuleItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;
            List<string> origins = BuildOriginTexts(text, name, lookahead, names);
            if (origins.Count == 0)
                continue;
            items.Add(new ChallengeRuleItem
            {
                CompanyName = name,
                Count = origins.Count,
                OriginText = origins,
            });
        }

        return items;
    }

    /// <summary>
    /// For each occurrence of <paramref name="name"/> in <paramref name="pageText"/>,
    /// emit one excerpt of length in [10, 100] centered on the match when possible.
    /// Intra-CJK spaces are folded the same way as the reported name, so the excerpt
    /// contains that name. <paramref name="lookahead"/> is the start of the next
    /// non-empty page, used when a name is split by the page break.
    /// Hits covered by a longer name in <paramref name="coverNames"/> are skipped.
    /// </summary>
    public static List<string> BuildOriginTexts(
        string pageText,
        string name,
        string? lookahead = null,
        IReadOnlyList<string>? coverNames = null)
    {
        List<string> texts = [];
        if (string.IsNullOrEmpty(pageText) || string.IsNullOrEmpty(name))
            return texts;

        EntityText.PageIndex index = EntityText.PageIndex.Build(pageText, lookahead);
        foreach (EntityText.Hit hit in index.Find(name))
        {
            if (IsCovered(index, hit, name, coverNames))
                continue;
            string snippet = EntityText.Excerpt(index.Source, hit.OriginStart, hit.OriginEnd);
            if (snippet.Length == 0)
                continue;
            if (snippet.Length > OriginMaxLen)
                snippet = snippet[..OriginMaxLen];
            texts.Add(snippet);
        }

        return texts;
    }

    private static bool IsCovered(
        EntityText.PageIndex index,
        EntityText.Hit hit,
        string name,
        IReadOnlyList<string>? coverNames)
    {
        if (coverNames is null)
            return false;
        foreach (string other in coverNames)
        {
            if (string.IsNullOrEmpty(other) || other.Length <= name.Length)
                continue;
            if (!EntityText.Identity(other).Contains(EntityText.Identity(name), StringComparison.Ordinal))
                continue;
            foreach (EntityText.Hit outer in index.Find(other))
            {
                if (EntityText.Covers(outer, hit))
                    return true;
            }
        }

        return false;
    }
}
