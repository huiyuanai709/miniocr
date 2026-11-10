using System.Text.Json.Serialization;

namespace MiniOcr.Models;

/// <summary>Platform → service challenge request (no auth).</summary>
public sealed class ChallengeRequest
{
    public int TeamId { get; set; }
    public string? Key { get; set; }
    public string? CallbackUrl { get; set; }
    public List<ChallengeFileRef>? Files { get; set; }

    /// <summary>
    /// Optional 1-based pages for every file in this job. Same syntax as <c>POST /ocr</c>
    /// (<c>1-10</c>, <c>5-12</c>, <c>1,3,5-8</c>, or a JSON array). Omit to OCR each whole PDF.
    /// </summary>
    [JsonConverter(typeof(PdfPageSpecJsonConverter))]
    public string? Pages { get; set; }
}

public sealed class ChallengeFileRef
{
    public string? FileId { get; set; }
    public string? Url { get; set; }
}

/// <summary>Immediate HTTP 200 body.</summary>
public sealed class ChallengeAckResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
}

/// <summary>Service → platform callback body.</summary>
public sealed class ChallengeCallbackBody
{
    public int TeamId { get; set; }
    public string Key { get; set; } = "";
    public List<ChallengeFileResult> Result { get; set; } = [];
}

public sealed class ChallengeFileResult
{
    public string FileId { get; set; } = "";
    public List<ChallengePageResult> Pages { get; set; } = [];

    /// <summary>Set only when the job asked for a page range. Omitted for a whole-document job.</summary>
    public string? RequestedPages { get; set; }
    /// <summary>Pages actually processed (1-based, inclusive), after clamping.</summary>
    public string? ProcessedPages { get; set; }
    /// <summary>PDF page count. Set only when a page range was requested.</summary>
    public int? DocumentPages { get; set; }
    /// <summary>True when the PDF was shorter than the request.</summary>
    public bool? PagesClamped { get; set; }
    /// <summary>How the PDF was obtained (<c>http-range</c>, <c>local-file</c>, …). Set only with a page range.</summary>
    public string? DownloadMode { get; set; }
    /// <summary>Bytes fetched or read. For <c>http-range</c> this is not the full content length.</summary>
    public long? BytesFetched { get; set; }
    /// <summary>HTTP Range reads. Set only for a Range session that also had a page range.</summary>
    public int? RangeReads { get; set; }
}

public sealed class ChallengePageResult
{
    public int Page { get; set; }
    public List<ChallengeRule> RuleList { get; set; } = [];
}

public sealed class ChallengeRule
{
    public string RuleCode { get; set; } = "";
    public string RuleName { get; set; } = "";
    public List<ChallengeRuleItem> RuleItemList { get; set; } = [];
}

/// <summary>
/// B04 items set <see cref="PersonName"/>; B06 items set <see cref="CompanyName"/>.
/// </summary>
public sealed class ChallengeRuleItem
{
    public string? PersonName { get; set; }
    public string? CompanyName { get; set; }
    /// <summary>Occurrence count. LLM payloads sometimes send this as a numeric string.</summary>
    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int Count { get; set; }
    public List<string> OriginText { get; set; } = [];
}
