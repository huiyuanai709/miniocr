using System.Text;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Repairs noisy OCR enough to match a name back to the page it came from.
/// Fullwidth ASCII folds to halfwidth. Whitespace that sits between Chinese
/// characters (or between a Chinese character and a bracket / middle dot / digit)
/// is removed, including line breaks. <see cref="Repair"/> does not substitute
/// characters. Company alignment may still accept one known OCR confusion via
/// <see cref="PageIndex.FindWithOcrConfusion"/>; the reported name is the corrected form.
/// Person names stay exact, because one character often distinguishes two people.
/// </summary>
public static class EntityText
{
    /// <summary>Characters of the next page joined when a name is cut by a page boundary.</summary>
    public const int CrossPageLookaheadChars = 240;

    public readonly record struct Hit(int NormStart, int NormEnd, int OriginStart, int OriginEnd);

    public static string Repair(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        (string text, _) = Normalize(raw, trimEdges: true);
        return text;
    }

    /// <summary>
    /// <see cref="Repair"/> plus one trailing courtesy title (先生 / 经理 / …).
    /// </summary>
    public static string RepairPerson(string? raw)
    {
        string s = Repair(raw);
        if (s.Length == 0)
            return "";
        foreach (string title in PersonTitles)
        {
            if (s.Length > title.Length + 1 && s.EndsWith(title, StringComparison.Ordinal))
            {
                s = s[..^title.Length].Trim();
                break;
            }
        }

        foreach (string title in PersonTitles)
        {
            if (s.Length > title.Length + 1 && s.StartsWith(title, StringComparison.Ordinal))
            {
                s = s[title.Length..].Trim();
                break;
            }
        }

        return s;
    }

    /// <summary>
    /// Identity used to dedup and to score. Trailing Inc/Ltd/Corp/Co dots are ignored.
    /// </summary>
    public static string Identity(string? raw)
    {
        string s = Repair(raw);
        if (s.Length > 2 && s[^1] == '.' && EndsWithAbbrevToken(s[..^1]))
            return s[..^1];
        return s;
    }

    public static bool Covers(Hit outer, Hit inner) =>
        outer.NormStart <= inner.NormStart &&
        outer.NormEnd >= inner.NormEnd &&
        outer.NormEnd - outer.NormStart > inner.NormEnd - inner.NormStart;

    public static string? Lookahead(IReadOnlyList<OcrPageResult> pages, int index)
    {
        for (int j = index + 1; j < pages.Count; j++)
        {
            string text = pages[j].Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
                continue;
            return text.Length <= CrossPageLookaheadChars ? text : text[..CrossPageLookaheadChars];
        }

        return null;
    }

    /// <summary>
    /// 10–100 character repaired excerpt of <paramref name="source"/> that contains
    /// the repaired match. The window may extend into a following page when
    /// <paramref name="source"/> is page text plus lookahead.
    /// </summary>
    public static string Excerpt(string source, int originStart, int originEnd)
    {
        if (string.IsNullOrEmpty(source) || originEnd <= originStart || originStart < 0 || originStart >= source.Length)
            return "";
        originEnd = Math.Min(source.Length, originEnd);
        string name = Repair(source[originStart..originEnd]);
        if (name.Length == 0)
            return "";

        int left = originStart;
        int right = originEnd;
        string repaired = name;
        while (repaired.Length < ChallengeResultMapper.OriginMaxLen && (left > 0 || right < source.Length))
        {
            int step = Math.Max(8, ChallengeResultMapper.OriginMaxLen - repaired.Length);
            int nextLeft = Math.Max(0, left - step);
            int nextRight = Math.Min(source.Length, right + step);
            if (nextLeft == left && nextRight == right)
                break;
            left = nextLeft;
            right = nextRight;
            repaired = Repair(source[left..right]);
        }

        if (!repaired.Contains(name, StringComparison.Ordinal))
            repaired = name;
        return WindowContaining(repaired, name, ChallengeResultMapper.OriginMinLen, ChallengeResultMapper.OriginMaxLen);
    }

    public sealed class PageIndex
    {
        private PageIndex(string source, int pageLength, string norm, int[] origin)
        {
            Source = source;
            PageLength = pageLength;
            Norm = norm;
            Origin = origin;
        }

        public string Source { get; }
        public int PageLength { get; }
        public string Norm { get; }
        public int[] Origin { get; }

        public static PageIndex Build(string? pageText, string? lookahead)
        {
            pageText ??= "";
            string source = string.IsNullOrEmpty(lookahead) ? pageText : pageText + "\n" + lookahead;
            (string norm, int[] origin) = Normalize(source, trimEdges: false);
            return new PageIndex(source, pageText.Length, norm, origin);
        }

        public List<Hit> Find(string? name)
        {
            string needle = Repair(name);
            List<Hit> hits = [];
            if (needle.Length == 0 || Norm.Length < needle.Length)
                return hits;

            int from = 0;
            while (from <= Norm.Length - needle.Length)
            {
                int idx = Norm.IndexOf(needle, from, StringComparison.Ordinal);
                if (idx < 0)
                    break;
                if (TryMakeHit(idx, needle, out Hit hit))
                    hits.Add(hit);
                from = idx + needle.Length;
            }

            return hits;
        }

        /// <summary>
        /// Exact matches, or — when the corrected company never occurs verbatim —
        /// spans that differ by exactly one known OCR lookalike.
        /// Names shorter than 6 characters stay exact.
        /// </summary>
        public List<Hit> FindWithOcrConfusion(string? name)
        {
            List<Hit> exact = Find(name);
            string needle = Repair(name);
            if (exact.Count > 0 || needle.Length < 6 || Norm.Length < needle.Length)
                return exact;

            List<Hit> hits = [];
            for (int i = 0; i <= Norm.Length - needle.Length; i++)
            {
                if (!IsSingleOcrConfusion(Norm.AsSpan(i, needle.Length), needle.AsSpan()))
                    continue;
                if (TryMakeHit(i, needle, out Hit hit))
                    hits.Add(hit);
                i += needle.Length - 1;
            }

            return hits;
        }

        private bool TryMakeHit(int idx, string needle, out Hit hit)
        {
            hit = default;
            int last = idx + needle.Length - 1;
            if ((uint)last >= (uint)Origin.Length)
                return false;
            int originStart = Origin[idx];
            int originEnd = Origin[last] + 1;
            if (originEnd < Source.Length &&
                Fold(Source[originEnd]) == '.' &&
                EndsWithAbbrevToken(needle))
            {
                originEnd++;
            }

            if (originStart >= PageLength)
                return false;
            hit = new Hit(idx, idx + needle.Length, originStart, originEnd);
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="surface"/> and <paramref name="corrected"/> have the
    /// same length (at least 6) and differ by exactly one OCR lookalike pair.
    /// </summary>
    internal static bool IsSingleOcrConfusion(string surface, string corrected) =>
        surface.Length == corrected.Length &&
        IsSingleOcrConfusion(surface.AsSpan(), corrected.AsSpan());

    private static bool IsSingleOcrConfusion(ReadOnlySpan<char> surface, ReadOnlySpan<char> corrected)
    {
        if (surface.Length != corrected.Length || surface.Length < 6)
            return false;
        int diffs = 0;
        for (int i = 0; i < surface.Length; i++)
        {
            if (surface[i] == corrected[i])
                continue;
            if (diffs == 1 || !IsOcrConfusion(surface[i], corrected[i]))
                return false;
            diffs = 1;
        }

        return diffs == 1;
    }

    /// <summary>
    /// Rewrites lookalike windows in an origin snippet so it contains the corrected name.
    /// </summary>
    internal static string PreferCorrected(string snippet, string corrected)
    {
        if (string.IsNullOrEmpty(snippet) || string.IsNullOrEmpty(corrected))
            return snippet;
        string needle = Repair(corrected);
        if (needle.Length < 6 || snippet.Contains(needle, StringComparison.Ordinal) || snippet.Length < needle.Length)
            return snippet;

        StringBuilder sb = new(snippet.Length);
        int i = 0;
        bool any = false;
        while (i <= snippet.Length - needle.Length)
        {
            if (IsSingleOcrConfusion(snippet.AsSpan(i, needle.Length), needle.AsSpan()))
            {
                sb.Append(needle);
                i += needle.Length;
                any = true;
                continue;
            }

            sb.Append(snippet[i]);
            i++;
        }

        if (!any)
            return snippet;
        if (i < snippet.Length)
            sb.Append(snippet.AsSpan(i));
        return sb.ToString();
    }

    private static bool IsOcrConfusion(char a, char b) =>
        a != b && OcrConfusions.Contains(Pack(a, b));

    private static int Pack(char a, char b) => ((int)a << 16) | b;

    /// <summary>
    /// Lookalike pairs that company alignment may treat as one OCR error.
    /// 北/上 and 张/王 are different names and are not in this set.
    /// </summary>
    private static readonly string[] OcrConfusionPairs =
    [
        "司同", "司可",
        "术木", "份分", "技枝",
        "己已", "己巳", "已巳",
        "日曰", "未末", "人入", "土士", "干千",
        "微徵", "拨拔",
        "戊戌", "戊戍", "戌戍",
        "银很", "行形", "有右", "厂广", "大太",
    ];

    private static readonly HashSet<int> OcrConfusions = CreateOcrConfusions();

    private static HashSet<int> CreateOcrConfusions()
    {
        HashSet<int> set = [];
        foreach (string pair in OcrConfusionPairs)
        {
            set.Add(Pack(pair[0], pair[1]));
            set.Add(Pack(pair[1], pair[0]));
        }

        return set;
    }

    private static string WindowContaining(string text, string name, int minLen, int maxLen)
    {
        if (text.Length == 0)
            return "";
        if (text.Length <= maxLen && (text.Length >= minLen || text.Contains(name, StringComparison.Ordinal)))
        {
            if (text.Length <= maxLen)
                return text;
        }

        int at = text.IndexOf(name, StringComparison.Ordinal);
        if (at < 0)
            return text.Length <= maxLen ? text : text[..maxLen];

        if (name.Length >= maxLen)
            return name[..maxLen];

        int target = Math.Min(maxLen, Math.Max(minLen, Math.Min(maxLen, text.Length)));
        if (text.Length <= target)
            return text;

        int extra = target - name.Length;
        int leftPad = extra / 2;
        int start = at - leftPad;
        if (start < 0)
            start = 0;
        if (start + target > text.Length)
            start = text.Length - target;
        if (start > at)
            start = at;
        int end = Math.Min(text.Length, start + target);
        if (end < at + name.Length)
            end = Math.Min(text.Length, at + name.Length);
        if (end - start > maxLen)
            start = end - maxLen;
        if (start < 0)
            start = 0;
        return text[start..end];
    }

    private static (string Text, int[] Origin) Normalize(string raw, bool trimEdges)
    {
        if (raw.Length == 0)
            return ("", []);

        List<char> chars = new(raw.Length);
        List<int> origins = new(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char folded = Fold(raw[i]);
            if (IsWhitespace(folded))
            {
                int j = i + 1;
                while (j < raw.Length && IsWhitespace(Fold(raw[j])))
                    j++;
                char prev = chars.Count > 0 ? chars[^1] : '\0';
                char next = j < raw.Length ? Fold(raw[j]) : '\0';
                if (!ShouldDropWhitespace(prev, next) && chars.Count > 0 && next != '\0')
                {
                    chars.Add(' ');
                    origins.Add(i);
                }

                i = j;
                continue;
            }

            chars.Add(folded);
            origins.Add(i);
            i++;
        }

        int start = 0;
        int end = chars.Count;
        if (trimEdges)
        {
            while (start < end && IsEdgePunct(chars[start]))
                start++;
            while (end > start && IsEdgePunct(chars[end - 1]))
            {
                if (chars[end - 1] == '.' && KeepTrailingDot(chars, end - 1))
                    break;
                end--;
            }

            if (start < end - 1 && chars[start] == '(' && chars[end - 1] == ')')
            {
                start++;
                end--;
                while (start < end && IsEdgePunct(chars[start]))
                    start++;
                while (end > start && IsEdgePunct(chars[end - 1]))
                {
                    if (chars[end - 1] == '.' && KeepTrailingDot(chars, end - 1))
                        break;
                    end--;
                }
            }
        }

        int n = end - start;
        if (n <= 0)
            return ("", []);
        char[] text = new char[n];
        int[] origin = new int[n];
        for (int k = 0; k < n; k++)
        {
            text[k] = chars[start + k];
            origin[k] = origins[start + k];
        }

        return (new string(text), origin);
    }

    private static bool ShouldDropWhitespace(char prev, char next)
    {
        if (prev == '\0' || next == '\0')
            return true;
        if (!IsGlue(prev) || !IsGlue(next))
            return false;
        return IsCjk(prev) || IsCjk(next);
    }

    private static bool KeepTrailingDot(List<char> chars, int dotIndex)
    {
        int from = Math.Max(0, dotIndex - 16);
        string head = new(chars.ToArray(), from, dotIndex - from);
        return EndsWithAbbrevToken(head);
    }

    internal static bool EndsWithAbbrevToken(string s)
    {
        return EndsWithToken(s, "Corporation")
            || EndsWithToken(s, "Company")
            || EndsWithToken(s, "Corp")
            || EndsWithToken(s, "LLC")
            || EndsWithToken(s, "Ltd")
            || EndsWithToken(s, "Inc")
            || EndsWithToken(s, "Co");
    }

    private static bool EndsWithToken(string s, string token)
    {
        if (!s.EndsWith(token, StringComparison.OrdinalIgnoreCase))
            return false;
        if (s.Length == token.Length)
            return true;
        char before = s[s.Length - token.Length - 1];
        return before is ' ' or ',' or '.';
    }

    internal static char Fold(char c)
    {
        if (c == '\u3000')
            return ' ';
        if (c is >= '\uFF01' and <= '\uFF5E')
            return (char)(c - 0xFEE0);
        if (c is '•' or '・')
            return '·';
        return c;
    }

    private static bool IsWhitespace(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    private static bool IsEdgePunct(char c) =>
        c is ' ' or '，' or ',' or '。' or '.' or '；' or ';' or '：' or ':' or '、'
            or '"' or '\'' or '“' or '”' or '‘' or '’' or '《' or '》' or '<' or '>'
            or '【' or '】' or '[' or ']';

    internal static bool IsGlue(char c) =>
        IsCjk(c) || char.IsDigit(c) || c is '(' or ')' or '[' or ']' or '【' or '】' or '「' or '」' or '·';

    internal static bool IsCjk(char c) =>
        c is (>= '\u4e00' and <= '\u9fff') or (>= '\u3400' and <= '\u4dbf');

    private static readonly string[] PersonTitles =
    [
        "委托诉讼代理人", "诉讼代理人", "委托代理人",
        "副总经理", "副董事长", "总经理", "董事长", "副总裁",
        "人民陪审员", "审判长", "审判员", "书记员",
        "女士", "先生", "同志",
        "经理", "董事", "主任", "局长", "处长", "科长", "总监", "总裁",
    ];
}
