using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Turns raw LLM name lists into protocol entities.
/// The model is asked to read the whole context and to correct OCR noise.
/// This step still does not invent names with a regex scan. It drops masked
/// names, roles, courts and other non-companies, and short names that are only
/// a prefix of a longer legal name.
/// A name is kept when it aligns after whitespace and fullwidth repair, including
/// the next page's head. A company may also align when the page differs by exactly
/// one character from <see cref="EntityText"/>'s OCR confusion list (for example
/// 公同 / 公司); the reported name is the corrected form, and the origin snippet
/// is rewritten so it contains that form. A person is kept only when the exact
/// name appears, so a one-character guess such as 张纬 → 张伟 is dropped.
/// </summary>
public static class EntityPostProcessor
{
    public static OcrEntities Merge(
        IReadOnlyList<OcrPageResult> pages,
        IEnumerable<string> companies,
        IEnumerable<string> persons)
    {
        List<string> companyNames = Collect(companies, person: false);
        List<string> personNames = Collect(persons, person: true);

        companyNames = Dedup(companyNames.Select(name => ExpandLegalForm(pages, name)));
        companyNames = companyNames.Where(name => HasHit(pages, name, allowOcrConfusion: true)).ToList();
        personNames = personNames.Where(name => HasHit(pages, name, allowOcrConfusion: false)).ToList();
        companyNames = SuppressShortCompanies(companyNames);
        personNames = personNames.Where(name => HasUncoveredPersonHit(pages, name, companyNames, personNames)).ToList();
        companyNames = companyNames.Where(name => HasUncoveredCompanyHit(pages, name, companyNames)).ToList();

        return new OcrEntities
        {
            Companies = BuildHits(pages, companyNames, companyNames, allowOcrConfusion: true),
            Persons = BuildHits(pages, personNames, CoverNamesForPersons(personNames, companyNames), allowOcrConfusion: false),
        };
    }

    public static bool IsAcceptablePerson(string name)
    {
        if (name.Length is < 2 or > 40)
            return false;
        if (name.Contains('某', StringComparison.Ordinal))
            return false;
        if (RoleWords.Contains(name))
            return false;
        if (IsAcceptableCompany(name))
            return false;

        int cjk = 0;
        int letters = 0;
        foreach (char c in name)
        {
            if (EntityText.IsCjk(c))
                cjk++;
            else if (char.IsLetter(c))
                letters++;
        }

        if (cjk == 0 && letters == 0)
            return false;
        if (letters == 0 && cjk < 2)
            return false;
        if (letters == 0 && !name.Contains('·', StringComparison.Ordinal) && cjk > 6)
            return false;
        if (letters == 0 && name.Contains('·', StringComparison.Ordinal) && cjk > 16)
            return false;
        return true;
    }

    public static bool IsAcceptableCompany(string name)
    {
        if (name.Length < 4 || name.Length > 80)
            return false;
        if (GenericCompanies.Contains(name))
            return false;
        if (ContainsGovernment(name) && !name.Contains("公司", StringComparison.Ordinal))
            return false;
        if (EndsWithInstitution(name) && !name.Contains("公司", StringComparison.Ordinal))
            return false;
        if (EndsWithChineseOrg(name))
            return true;
        return EntityText.EndsWithAbbrevToken(name.EndsWith('.') ? name[..^1] : name);
    }

    private static List<string> Collect(IEnumerable<string> raw, bool person)
    {
        List<string> list = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string item in raw)
        {
            string name = person ? EntityText.RepairPerson(item) : EntityText.Repair(item);
            if (name.Length == 0)
                continue;
            if (person)
            {
                if (!IsAcceptablePerson(name))
                    continue;
            }
            else if (!IsAcceptableCompany(name))
            {
                continue;
            }

            if (seen.Add(EntityText.Identity(name)))
                list.Add(name);
        }

        return list;
    }

    /// <summary>
    /// When the model returns a short form and the OCR text continues with
    /// 股份有限公司 / 有限责任公司 / 有限公司, keep that longer surface.
    /// A following 分公司 / 分行 is a different entity and is not swallowed.
    /// </summary>
    private static string ExpandLegalForm(IReadOnlyList<OcrPageResult> pages, string name)
    {
        if (name.EndsWith("公司", StringComparison.Ordinal) ||
            name.EndsWith("事务所", StringComparison.Ordinal) ||
            name.EndsWith("合伙企业", StringComparison.Ordinal) ||
            name.EndsWith("合作社", StringComparison.Ordinal))
        {
            return name;
        }

        string best = name;
        for (int i = 0; i < pages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(pages[i].Text))
                continue;
            EntityText.PageIndex index = EntityText.PageIndex.Build(pages[i].Text, EntityText.Lookahead(pages, i));
            foreach (EntityText.Hit hit in index.Find(name))
            {
                if (!HasLeftBoundary(index.Norm, hit.NormStart))
                    continue;
                foreach (string suffix in LegalFormSuffixes)
                {
                    int end = hit.NormEnd;
                    if (end + suffix.Length > index.Norm.Length)
                        continue;
                    if (!index.Norm.AsSpan(end, suffix.Length).SequenceEqual(suffix))
                        continue;
                    string extended = index.Norm.Substring(hit.NormStart, hit.NormEnd - hit.NormStart + suffix.Length);
                    if (extended.Length > best.Length)
                        best = extended;
                    break;
                }
            }
        }

        return best;
    }

    private static bool HasLeftBoundary(string norm, int start)
    {
        if (start <= 0)
            return true;
        char prev = norm[start - 1];
        return !EntityText.IsCjk(prev) && prev != '·' && !char.IsDigit(prev);
    }

    private static List<string> Dedup(IEnumerable<string> names)
    {
        List<string> list = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (name.Length > 0 && seen.Add(EntityText.Identity(name)))
                list.Add(name);
        }

        return list;
    }

    private static bool HasHit(IReadOnlyList<OcrPageResult> pages, string name, bool allowOcrConfusion)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(pages[i].Text))
                continue;
            EntityText.PageIndex index = EntityText.PageIndex.Build(pages[i].Text, EntityText.Lookahead(pages, i));
            List<EntityText.Hit> hits = allowOcrConfusion ? index.FindWithOcrConfusion(name) : index.Find(name);
            if (hits.Count > 0)
                return true;
        }

        return false;
    }

    private static bool HasUncoveredPersonHit(
        IReadOnlyList<OcrPageResult> pages,
        string name,
        IReadOnlyList<string> companies,
        IReadOnlyList<string> persons)
    {
        List<string> covers = CoverNamesForPersons(persons, companies);
        return CountUncovered(pages, name, covers, allowOcrConfusion: false) > 0;
    }

    private static bool HasUncoveredCompanyHit(
        IReadOnlyList<OcrPageResult> pages,
        string name,
        IReadOnlyList<string> companies) =>
        CountUncovered(pages, name, companies, allowOcrConfusion: true) > 0;

    private static List<string> CoverNamesForPersons(IReadOnlyList<string> persons, IReadOnlyList<string> companies)
    {
        List<string> covers = new(companies.Count + persons.Count);
        covers.AddRange(companies);
        covers.AddRange(persons);
        return covers;
    }

    private static int CountUncovered(
        IReadOnlyList<OcrPageResult> pages,
        string name,
        IReadOnlyList<string> covers,
        bool allowOcrConfusion)
    {
        int total = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(pages[i].Text))
                continue;
            EntityText.PageIndex index = EntityText.PageIndex.Build(pages[i].Text, EntityText.Lookahead(pages, i));
            total += Uncovered(index, name, covers, allowOcrConfusion).Count;
        }

        return total;
    }

    private static List<EntityText.Hit> Uncovered(
        EntityText.PageIndex index,
        string name,
        IReadOnlyList<string> covers,
        bool allowOcrConfusion)
    {
        List<EntityText.Hit> hits = allowOcrConfusion ? index.FindWithOcrConfusion(name) : index.Find(name);
        if (hits.Count == 0 || covers.Count == 0)
            return hits;

        List<EntityText.Hit> kept = [];
        foreach (EntityText.Hit hit in hits)
        {
            bool covered = false;
            foreach (string other in covers)
            {
                if (other.Length <= name.Length || !EntityText.Identity(other).Contains(EntityText.Identity(name), StringComparison.Ordinal))
                    continue;
                foreach (EntityText.Hit outer in index.Find(other))
                {
                    if (EntityText.Covers(outer, hit))
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered)
                    break;
            }

            if (!covered)
                kept.Add(hit);
        }

        return kept;
    }

    /// <summary>
    /// Drop a shorter company that is contained in a longer one, unless the longer
    /// name is that company plus a branch (分公司 / 分行 / …). Those are different entities.
    /// </summary>
    private static List<string> SuppressShortCompanies(List<string> companies)
    {
        if (companies.Count <= 1)
            return companies;

        List<string> ordered = companies.OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();
        List<string> kept = [];
        foreach (string name in ordered)
        {
            bool drop = false;
            foreach (string longer in kept)
            {
                if (longer.Length > name.Length &&
                    longer.Contains(name, StringComparison.Ordinal) &&
                    !IsDistinctBranch(longer, name))
                {
                    drop = true;
                    break;
                }
            }

            if (!drop)
                kept.Add(name);
        }

        List<string> stable = [];
        foreach (string name in companies)
        {
            if (kept.Contains(name, StringComparer.Ordinal))
                stable.Add(name);
        }

        return stable;
    }

    private static bool IsDistinctBranch(string longer, string shorter)
    {
        if (!longer.StartsWith(shorter, StringComparison.Ordinal))
            return false;
        string rest = longer[shorter.Length..];
        foreach (string marker in BranchMarkers)
        {
            if (rest.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static List<EntityHit> BuildHits(
        IReadOnlyList<OcrPageResult> pages,
        IReadOnlyList<string> names,
        IReadOnlyList<string> covers,
        bool allowOcrConfusion)
    {
        List<EntityHit> hits = [];
        foreach (string name in names)
        {
            HashSet<int> pageSet = [];
            int count = 0;
            string display = name;
            bool haveDisplay = false;
            for (int i = 0; i < pages.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(pages[i].Text))
                    continue;
                EntityText.PageIndex index = EntityText.PageIndex.Build(pages[i].Text, EntityText.Lookahead(pages, i));
                List<EntityText.Hit> found = Uncovered(index, name, covers, allowOcrConfusion);
                if (found.Count == 0)
                    continue;
                count += found.Count;
                pageSet.Add(pages[i].Page);
                if (!haveDisplay)
                {
                    EntityText.Hit first = found[0];
                    int end = Math.Min(index.Source.Length, first.OriginEnd);
                    if (end > first.OriginStart)
                        display = EntityText.Repair(index.Source[first.OriginStart..end]);
                    if (display.Length == 0)
                        display = name;
                    if (allowOcrConfusion &&
                        !string.Equals(display, name, StringComparison.Ordinal) &&
                        EntityText.IsSingleOcrConfusion(display, name))
                    {
                        display = name;
                    }

                    haveDisplay = true;
                }
            }

            if (count == 0)
                continue;
            hits.Add(new EntityHit
            {
                Name = display,
                Pages = pageSet.OrderBy(p => p).ToList(),
                Count = count,
            });
        }

        hits.Sort(static (a, b) =>
        {
            int c = b.Count.CompareTo(a.Count);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        return hits;
    }

    private static bool EndsWithChineseOrg(string name)
    {
        foreach (string suffix in ChineseOrgSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool EndsWithInstitution(string name)
    {
        foreach (string suffix in InstitutionSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsGovernment(string name)
    {
        foreach (string marker in GovernmentMarkers)
        {
            if (name.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static readonly string[] ChineseOrgSuffixes =
    [
        "合伙企业", "合作社", "事务所", "信用社", "公司", "集团", "银行", "厂",
    ];

    private static readonly string[] InstitutionSuffixes =
    [
        "大学", "学院", "医院", "学校", "中学", "小学", "研究院", "研究所",
    ];

    private static readonly string[] GovernmentMarkers =
    [
        "人民法院", "人民检察院", "派出所", "公安局", "公安厅", "人民政府",
        "司法局", "监督管理局", "仲裁委员会", "监察委员会", "管委会",
        "管理委员会", "街道办事处",
    ];

    private static readonly string[] LegalFormSuffixes =
    [
        "股份有限公司", "有限责任公司", "有限公司", "股份公司",
    ];

    private static readonly string[] BranchMarkers =
    [
        "分公司", "支公司", "分行", "支行", "营业部", "办事处",
    ];

    private static readonly HashSet<string> GenericCompanies = new(StringComparer.Ordinal)
    {
        "公司", "有限公司", "股份有限公司", "有限责任公司", "集团", "集团公司",
        "银行", "分公司", "本公司", "该公司", "我公司", "贵公司",
    };

    private static readonly HashSet<string> RoleWords = new(StringComparer.Ordinal)
    {
        "原告", "被告", "第三人", "甲方", "乙方", "丙方", "丁方", "买方", "卖方",
        "出卖人", "买受人", "法定代表人", "委托人", "受托人", "代理人",
        "委托代理人", "诉讼代理人", "委托诉讼代理人", "审判长", "审判员",
        "人民陪审员", "书记员", "申请人", "被申请人", "申请执行人", "被执行人",
        "本公司", "该公司", "本人", "我方", "对方", "贵方", "先生", "女士", "同志",
        "经理", "总经理", "董事长", "联系人", "经办人", "负责人", "当事人",
    };
}
