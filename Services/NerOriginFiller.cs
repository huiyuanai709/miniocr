using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Fills <c>originText</c> the model no longer emits. Names and counts stay as the
/// model wrote them. A name the page text does not contain keeps an empty snippet list.
/// </summary>
public static class NerOriginFiller
{
    public static void Apply(
        IList<ChallengePageResult> modelPages,
        IReadOnlyList<OcrPageResult> document,
        string? boundaryLookahead = null,
        int boundaryLookaheadPage = 0)
    {
        if (modelPages.Count == 0)
            return;

        SortedDictionary<int, string> text = [];
        foreach (OcrPageResult page in document)
        {
            if (page.Page > 0 && !string.IsNullOrEmpty(page.Text))
                text[page.Page] = page.Text;
        }

        if (!string.IsNullOrEmpty(boundaryLookahead) && boundaryLookaheadPage > 0 && !text.ContainsKey(boundaryLookaheadPage))
            text[boundaryLookaheadPage] = boundaryLookahead;

        if (text.Count == 0)
            return;

        foreach (ChallengePageResult page in modelPages)
        {
            if (page.RuleList is null || !text.TryGetValue(page.Page, out string? body))
                continue;

            string? lookahead = NextHead(text, page.Page);
            List<string> companies = Names(page, person: false);
            List<string> persons = Names(page, person: true);
            foreach (ChallengeRule rule in page.RuleList)
            {
                bool person = string.Equals(rule.RuleCode, "B04", StringComparison.OrdinalIgnoreCase);
                if (rule.RuleItemList is null)
                    continue;
                foreach (ChallengeRuleItem item in rule.RuleItemList)
                {
                    if (item.OriginText is { Count: > 0 })
                        continue;
                    string name = person ? item.PersonName ?? "" : item.CompanyName ?? "";
                    if (name.Length == 0)
                        continue;
                    List<string> covers = person ? CoverPersons(name, persons, companies) : companies;
                    List<string> origins = ChallengeResultMapper.BuildOriginTexts(
                        body,
                        name,
                        lookahead,
                        covers,
                        allowOcrConfusion: !person);
                    if (origins.Count > 0)
                        item.OriginText = origins;
                }
            }
        }
    }

    private static string? NextHead(SortedDictionary<int, string> text, int page)
    {
        foreach (KeyValuePair<int, string> entry in text)
        {
            if (entry.Key <= page || string.IsNullOrEmpty(entry.Value))
                continue;
            return entry.Value.Length <= EntityText.CrossPageLookaheadChars
                ? entry.Value
                : entry.Value[..EntityText.CrossPageLookaheadChars];
        }

        return null;
    }

    private static List<string> Names(ChallengePageResult page, bool person)
    {
        List<string> names = [];
        if (page.RuleList is null)
            return names;
        foreach (ChallengeRule rule in page.RuleList)
        {
            bool rulePerson = string.Equals(rule.RuleCode, "B04", StringComparison.OrdinalIgnoreCase);
            if (rulePerson != person || rule.RuleItemList is null)
                continue;
            foreach (ChallengeRuleItem item in rule.RuleItemList)
            {
                string name = person ? item.PersonName ?? "" : item.CompanyName ?? "";
                if (name.Length > 0)
                    names.Add(name);
            }
        }

        return names;
    }

    private static List<string> CoverPersons(string name, List<string> persons, List<string> companies)
    {
        List<string> covers = new(companies.Count + persons.Count);
        covers.AddRange(companies);
        foreach (string other in persons)
        {
            if (other.Length > name.Length)
                covers.Add(other);
        }

        return covers;
    }
}
