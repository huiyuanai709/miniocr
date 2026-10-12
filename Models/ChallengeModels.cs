using System.Text.Json.Serialization;

namespace MiniOcr.Models;

/// <summary>Platform → service challenge request (no auth).</summary>
public sealed class ChallengeRequest
{
    public int TeamId { get; set; }
    public string? Key { get; set; }
    public string? CallbackUrl { get; set; }
    public List<ChallengeFileRef>? Files { get; set; }
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
