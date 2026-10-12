using System.Text.Json;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Compact per-page NER reply: <c>{"p":[{"n":1,"c":[["公司",2]],"r":[["张伟",1]]}]}</c>.
/// <c>c</c> is companies (B06), <c>r</c> is persons (B04). Each hit is
/// <c>[name, count]</c>, a bare name, or <c>{"n":name,"c":count}</c>.
/// Snippets are not part of this shape; the caller fills them from the page text.
/// </summary>
public static class CompactNer
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static bool TryParse(string? json, out List<ChallengePageResult> pages)
    {
        pages = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, Lenient);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("p", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement page in list.EnumerateArray())
            {
                if (page.ValueKind != JsonValueKind.Object)
                    continue;
                int number = ReadPageNumber(page);
                if (number <= 0)
                    continue;

                List<ChallengeRuleItem> companies = ReadHits(page, "c", "companies", person: false);
                List<ChallengeRuleItem> persons = ReadHits(page, "r", "persons", person: true);
                List<ChallengeRule> rules = [];
                if (persons.Count > 0)
                    rules.Add(new ChallengeRule { RuleCode = "B04", RuleName = "人员名称", RuleItemList = persons });
                if (companies.Count > 0)
                    rules.Add(new ChallengeRule { RuleCode = "B06", RuleName = "公司名称", RuleItemList = companies });
                if (rules.Count == 0)
                    continue;
                pages.Add(new ChallengePageResult { Page = number, RuleList = rules });
            }

            return true;
        }
    }

    private static int ReadPageNumber(JsonElement page)
    {
        if (TryProperty(page, out JsonElement n, "n", "page"))
            return ReadPositiveInt(n);
        return 0;
    }

    private static List<ChallengeRuleItem> ReadHits(JsonElement page, string primary, string alias, bool person)
    {
        List<ChallengeRuleItem> items = [];
        if (!TryProperty(page, out JsonElement list, primary, alias) || list.ValueKind != JsonValueKind.Array)
            return items;

        foreach (JsonElement hit in list.EnumerateArray())
        {
            if (!TryReadHit(hit, out string name, out int count))
                continue;
            items.Add(new ChallengeRuleItem
            {
                PersonName = person ? name : null,
                CompanyName = person ? null : name,
                Count = count,
                OriginText = [],
            });
        }

        return items;
    }

    private static bool TryReadHit(JsonElement hit, out string name, out int count)
    {
        name = "";
        count = 1;
        switch (hit.ValueKind)
        {
            case JsonValueKind.String:
                name = hit.GetString()?.Trim() ?? "";
                return name.Length > 0;
            case JsonValueKind.Array:
                if (hit.GetArrayLength() < 1)
                    return false;
                name = ReadLooseString(hit[0]);
                if (hit.GetArrayLength() >= 2)
                    count = ReadPositiveInt(hit[1]);
                if (count <= 0)
                    count = 1;
                return name.Length > 0;
            case JsonValueKind.Object:
                if (!TryProperty(hit, out JsonElement named, "n", "name", "companyName", "personName", "company", "person"))
                    return false;
                name = ReadLooseString(named);
                if (TryProperty(hit, out JsonElement counted, "c", "count"))
                    count = ReadPositiveInt(counted);
                if (count <= 0)
                    count = 1;
                return name.Length > 0;
            default:
                return false;
        }
    }

    private static bool TryProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj.TryGetProperty(name, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static string ReadLooseString(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString()?.Trim() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            _ => "",
        };

    private static int ReadPositiveInt(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                if (el.TryGetInt32(out int n))
                    return n;
                if (el.TryGetDouble(out double d) && d > 0 && d < int.MaxValue)
                    return (int)d;
                return 0;
            case JsonValueKind.String:
                string? s = el.GetString()?.Trim();
                return int.TryParse(s, out int parsed) ? parsed : 0;
            default:
                return 0;
        }
    }
}
