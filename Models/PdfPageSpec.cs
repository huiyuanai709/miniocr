using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniOcr.Models;

/// <summary>
/// Which PDF pages to OCR. Page numbers are 1-based and inclusive, matching
/// <see cref="OcrPageResult.Page"/>. Omit the value to OCR the whole document.
/// A string (<c>1-10</c>, <c>5-12</c>, <c>1,3,5-8</c>) and a JSON array of those
/// numbers are the same list. Internally this becomes the 0-based indexes
/// PDFtoImage already renders (<c>first..(first+count)</c> or a page list).
/// </summary>
public sealed class PdfPageSpec
{
    private readonly (int Start, int End)[] _ranges;

    private PdfPageSpec((int Start, int End)[] ranges, string canonical)
    {
        _ranges = ranges;
        Canonical = canonical;
    }

    /// <summary>Compact form, for example <c>1-10</c> or <c>1,3,5-8</c>.</summary>
    public string Canonical { get; }

    /// <summary>
    /// <paramref name="text"/> null means the whole document.
    /// Empty, inverted, non-numeric, or a page below 1 throws <see cref="ArgumentException"/>.
    /// </summary>
    public static PdfPageSpec? Parse(string? text)
    {
        if (text is null)
            return null;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.IsEmpty)
            throw new ArgumentException("pages is empty.");

        List<(int Start, int End)> ranges = [];
        int i = 0;
        while (i < span.Length)
        {
            int comma = span[i..].IndexOf(',');
            ReadOnlySpan<char> token = (comma < 0 ? span[i..] : span.Slice(i, comma)).Trim();
            if (token.IsEmpty)
                throw new ArgumentException("pages contains an empty entry.");
            ranges.Add(ParseToken(token));
            if (comma < 0)
                break;
            i += comma + 1;
            if (i >= span.Length)
                throw new ArgumentException("pages contains an empty entry.");
        }

        return FromRanges(ranges);
    }

    /// <summary>
    /// Body wins when it is present (including an invalid value). A missing body falls
    /// through to the query string. Both missing means the whole document.
    /// </summary>
    public static PdfPageSpec? Combine(string? body, string? query) =>
        body is not null ? Parse(body) : Parse(query);

    public static PdfPageSpec FromPageNumbers(IReadOnlyList<int> pages)
    {
        if (pages is null || pages.Count == 0)
            throw new ArgumentException("pages is empty.");

        List<(int Start, int End)> ranges = new(pages.Count);
        foreach (int page in pages)
        {
            if (page < 1)
                throw new ArgumentException("pages start at 1.");
            if (page > MaxPage)
                throw new ArgumentException($"pages value {page} is not a page number.");
            ranges.Add((page, page));
        }

        return FromRanges(ranges);
    }

    /// <summary>
    /// Drop page numbers past <paramref name="documentPages"/>. A request that still
    /// overlaps the document is clamped (<c>1-10</c> on a 7-page PDF is pages 1-7).
    /// A request that falls entirely past the end throws.
    /// </summary>
    public PdfPageWindow Resolve(int documentPages)
    {
        if (documentPages <= 0)
            throw new ArgumentException("PDF has no pages.");

        List<int> zeroBased = [];
        bool clamped = false;
        foreach ((int start, int end) in _ranges)
        {
            if (start > documentPages)
            {
                clamped = true;
                continue;
            }

            int last = end;
            if (last > documentPages)
            {
                clamped = true;
                last = documentPages;
            }

            for (int page = start; page <= last; page++)
                zeroBased.Add(page - 1);
        }

        if (zeroBased.Count == 0)
        {
            throw new ArgumentException(
                $"Requested pages {Canonical} are outside this PDF ({documentPages} {(documentPages == 1 ? "page" : "pages")}).");
        }

        (int Start, int End)[] kept = Merge(ToInclusive(zeroBased));
        return new PdfPageWindow(documentPages, zeroBased.ToArray(), Canonical, Format(kept), clamped);
    }

    private const int MaxPage = 1_000_000;

    private static (int Start, int End) ParseToken(ReadOnlySpan<char> token)
    {
        int dash = token.IndexOf('-');
        if (dash < 0)
        {
            int page = ParsePage(token);
            return (page, page);
        }

        if (token[(dash + 1)..].Contains('-'))
            throw new ArgumentException($"pages value '{token.ToString()}' is not a page range.");

        ReadOnlySpan<char> left = token[..dash].Trim();
        ReadOnlySpan<char> right = token[(dash + 1)..].Trim();
        if (left.IsEmpty || right.IsEmpty)
            throw new ArgumentException($"pages value '{token.ToString()}' is not a page range.");

        int start = ParsePage(left);
        int end = ParsePage(right);
        if (end < start)
            throw new ArgumentException($"pages range {start}-{end} is inverted.");
        return (start, end);
    }

    private static int ParsePage(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || text[0] == '+' ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int page))
        {
            throw new ArgumentException($"pages value '{text.ToString()}' is not a page number.");
        }

        if (page < 1)
            throw new ArgumentException("pages start at 1.");
        if (page > MaxPage)
            throw new ArgumentException($"pages value {page} is not a page number.");
        return page;
    }

    private static PdfPageSpec FromRanges(List<(int Start, int End)> ranges)
    {
        (int Start, int End)[] merged = Merge(ranges);
        return new PdfPageSpec(merged, Format(merged));
    }

    private static (int Start, int End)[] Merge(List<(int Start, int End)> ranges)
    {
        ranges.Sort(static (a, b) =>
        {
            int byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : a.End.CompareTo(b.End);
        });

        List<(int Start, int End)> merged = [];
        foreach ((int start, int end) in ranges)
        {
            if (merged.Count == 0)
            {
                merged.Add((start, end));
                continue;
            }

            (int Start, int End) last = merged[^1];
            if (start <= last.End + 1)
                merged[^1] = (last.Start, Math.Max(last.End, end));
            else
                merged.Add((start, end));
        }

        return merged.ToArray();
    }

    private static List<(int Start, int End)> ToInclusive(List<int> zeroBased)
    {
        List<(int Start, int End)> ranges = [];
        int start = zeroBased[0] + 1;
        int end = start;
        for (int i = 1; i < zeroBased.Count; i++)
        {
            int page = zeroBased[i] + 1;
            if (page == end + 1)
            {
                end = page;
                continue;
            }

            ranges.Add((start, end));
            start = end = page;
        }

        ranges.Add((start, end));
        return ranges;
    }

    private static string Format(IReadOnlyList<(int Start, int End)> ranges)
    {
        StringBuilder sb = new();
        for (int i = 0; i < ranges.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            (int start, int end) = ranges[i];
            sb.Append(start.ToString(CultureInfo.InvariantCulture));
            if (end != start)
            {
                sb.Append('-');
                sb.Append(end.ToString(CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }
}

/// <summary>A <see cref="PdfPageSpec"/> after it has been fitted to one PDF.</summary>
public readonly record struct PdfPageWindow(
    int DocumentPages,
    int[] ZeroBased,
    string Requested,
    string Processed,
    bool Clamped);

/// <summary>
/// Accepts the string form and a JSON array (or one number) as the same page list.
/// </summary>
public sealed class PdfPageSpecJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        try
        {
            if (reader.TokenType == JsonTokenType.String)
                return PdfPageSpec.Parse(reader.GetString())?.Canonical;

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (!reader.TryGetInt32(out int page))
                    throw new JsonException("pages value is not a page number.");
                return PdfPageSpec.FromPageNumbers([page]).Canonical;
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                List<int> pages = [];
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                        break;
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int page))
                        throw new JsonException("pages array items must be integers.");
                    pages.Add(page);
                }

                return PdfPageSpec.FromPageNumbers(pages).Canonical;
            }
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }

        throw new JsonException(
            "pages must be a string such as \"1-10\" or \"5-12\" or \"1,3,5-8\", or an array of 1-based page numbers.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
