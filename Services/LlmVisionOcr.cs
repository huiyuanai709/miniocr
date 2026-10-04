using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MiniOcr.Models;
using SkiaSharp;

namespace MiniOcr.Services;

/// <summary>
/// OpenAI-compatible multimodal Chat Completions for per-page vision OCR.
/// POST {baseUrl}/v1/chat/completions with image_url data URLs.
/// AOT-safe: HttpClient + source-generated JSON. Never logs apiKey.
/// </summary>
public sealed class LlmVisionOcr
{
    private const string SystemPrompt =
        "You are a document OCR and entity extraction engine for Chinese and English PDFs. " +
        "Given one page image, return ONLY a strict json object (no markdown fences, no commentary) with shape:\n" +
        "{\"text\":\"full page plain text\",\"ruleList\":[" +
        "{\"ruleCode\":\"B04\",\"ruleName\":\"人员名称\",\"ruleItemList\":[" +
        "{\"personName\":\"...\",\"count\":1,\"originText\":[\"10-100 char snippet containing the name\"]}]}," +
        "{\"ruleCode\":\"B06\",\"ruleName\":\"公司名称\",\"ruleItemList\":[" +
        "{\"companyName\":\"...\",\"count\":1,\"originText\":[\"10-100 char snippet containing the company\"]}]}" +
        "]}\n" +
        "Rules:\n" +
        "- text: complete readable page text (preserve reading order; omit pure noise).\n" +
        "- B04 items use personName only; B06 items use companyName only.\n" +
        "- persons: natural names only. Strip 先生/女士/经理. No roles alone (原告, 甲方, 法定代表人), no pronouns, no masked names (张某, 李某某).\n" +
        "- companies: full legal names ending in 公司/集团/银行/事务所/合伙企业 or Inc/Ltd/LLC/Corp. " +
        "If both a short name and the full name are visible, output only the full name. " +
        "Keep a parent and its 分公司/分行 when both are visible.\n" +
        "- Do not output courts, procuratorates, governments, 公安/管理局/仲裁委员会, or universities/hospitals/schools unless the name contains 公司.\n" +
        "- Do not output product names, addresses, or project titles.\n" +
        "- Join characters split by spaces or line breaks. Do not invent characters that are not on the page.\n" +
        "- count = number of occurrences on this page; originText length 10–100 chars each and must contain the name.\n" +
        "- Do not invent names absent from the image. Empty ruleList is allowed if none found.\n" +
        "- Omit a rule entirely when its ruleItemList would be empty.";

    private readonly HttpClient _http;
    private readonly LlmRuntimeConfig _config;
    private readonly ILogger<LlmVisionOcr> _logger;
    private int _jsonObjectEnabled = 1;

    public LlmVisionOcr(
        HttpClient http,
        LlmRuntimeConfig config,
        ILogger<LlmVisionOcr> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public LlmRuntimeConfig Config => _config;
    public bool IsUsable => _config.IsUsable;
    public int OcrConcurrency => Math.Clamp(_config.OcrConcurrency, 1, 256);
    public int OcrJpegQuality => Math.Clamp(_config.OcrJpegQuality, 40, 95);

    /// <summary>Encode SKBitmap as JPEG bytes. Caller disposes bitmap.</summary>
    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality = 70)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 40, 95));
        return data.ToArray();
    }

    public async Task<OcrPageResult> RecognizePageAsync(
        int pageNumber,
        int width,
        int height,
        byte[] jpegBytes,
        double rasterMs,
        CancellationToken ct)
    {
        if (!IsUsable)
            throw new InvalidOperationException("LLM vision OCR is not configured (need enabled + apiKey).");

        Stopwatch sw = Stopwatch.StartNew();
        string dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);
        LlmVisionOcrPayload payload = await CompleteVisionAsync(pageNumber, dataUrl, ct)
            .ConfigureAwait(false);
        sw.Stop();

        string text = (payload.Text ?? "").Replace("\r", "").Trim();
        if (text.Length > _config.OcrMaxCharsHint)
            text = text[.._config.OcrMaxCharsHint];

        List<ChallengeRule>? rules = NormalizeRuleList(payload, text);

        return new OcrPageResult
        {
            Page = pageNumber,
            Width = width,
            Height = height,
            Text = text,
            RasterizeMs = Math.Round(rasterMs, 1),
            OcrMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            RuleList = rules is { Count: > 0 } ? rules : null,
        };
    }

    private async Task<LlmVisionOcrPayload> CompleteVisionAsync(
        int pageNumber,
        string imageDataUrl,
        CancellationToken ct)
    {
        Exception? parseError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await CompleteVisionOnceAsync(pageNumber, imageDataUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && LlmJson.IsParseFailure(ex) && !ct.IsCancellationRequested)
            {
                parseError = ex;
                _logger.LogWarning(ex, "LLM vision JSON parse failed for page {Page}; retrying once", pageNumber);
            }
        }

        throw parseError ?? new InvalidOperationException("LLM vision JSON parse failed.");
    }

    private async Task<LlmVisionOcrPayload> CompleteVisionOnceAsync(
        int pageNumber,
        string imageDataUrl,
        CancellationToken ct)
    {
        string url = _config.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
        string userText =
            $"OCR this PDF page (page {pageNumber}). " +
            $"Return full page text (aim ≤{_config.OcrMaxCharsHint} chars) and B04/B06 ruleList JSON as specified.";

        bool jsonMode = _config.JsonObject && Volatile.Read(ref _jsonObjectEnabled) == 1;
        VisionChatCompletionRequest body = new()
        {
            Model = _config.Model,
            Temperature = 0,
            Thinking = _config.ToThinkingOption(),
            ResponseFormat = jsonMode ? LlmJson.JsonObject : null,
            Messages =
            [
                new VisionChatMessage
                {
                    Role = "system",
                    Content = [new VisionContentPart { Type = "text", Text = SystemPrompt }],
                },
                new VisionChatMessage
                {
                    Role = "user",
                    Content =
                    [
                        new VisionContentPart { Type = "text", Text = userText },
                        new VisionContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new VisionImageUrl { Url = imageDataUrl },
                        },
                    ],
                },
            ],
        };

        _logger.LogInformation(
            "LLM vision OCR request: page={Page}, model={Model}, thinking={Thinking}, jsonObject={JsonObject}, url={Url}",
            pageNumber,
            _config.Model,
            body.Thinking?.Type ?? "(null)",
            jsonMode,
            url);

        string raw = await PostVisionAsync(url, body, jsonMode, ct).ConfigureAwait(false);
        ChatCompletionResponse? parsed =
            JsonSerializer.Deserialize(raw, AppJsonContext.Default.ChatCompletionResponse);
        string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("LLM vision OCR returned empty message content.");

        string json = NerPrompt.ExtractJsonObject(content);
        LlmVisionOcrPayload? payload = LlmJson.Deserialize<LlmVisionOcrPayload>(json);
        return payload ?? new LlmVisionOcrPayload();
    }

    private async Task<string> PostVisionAsync(
        string url,
        VisionChatCompletionRequest body,
        bool jsonMode,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(url, body, AppJsonContext.Default.VisionChatCompletionRequest, ct)
            .ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            && jsonMode
            && LlmJson.IsFormatRejected((int)response.StatusCode, raw))
        {
            Volatile.Write(ref _jsonObjectEnabled, 0);
            _logger.LogWarning(
                "LLM vision rejected response_format=json_object; disabling it and retrying without the field");
            body.ResponseFormat = null;
            using HttpResponseMessage retry = await _http
                .PostAsJsonAsync(url, body, AppJsonContext.Default.VisionChatCompletionRequest, ct)
                .ConfigureAwait(false);
            raw = await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!retry.IsSuccessStatusCode)
            {
                string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
                throw new HttpRequestException(
                    $"LLM vision OCR failed HTTP {(int)retry.StatusCode}: {snippet}");
            }

            return raw;
        }

        if (!response.IsSuccessStatusCode)
        {
            string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
            throw new HttpRequestException(
                $"LLM vision OCR failed HTTP {(int)response.StatusCode}: {snippet}");
        }

        return raw;
    }

    /// <summary>
    /// Prefer model ruleList; else synthesize B04/B06 from persons/companies + page text.
    /// </summary>
    internal static List<ChallengeRule>? NormalizeRuleList(LlmVisionOcrPayload payload, string pageText)
    {
        if (payload.RuleList is { Count: > 0 })
        {
            List<ChallengeRule> cleaned = [];
            foreach (ChallengeRule rule in payload.RuleList)
            {
                if (rule.RuleItemList is null || rule.RuleItemList.Count == 0)
                    continue;
                string code = (rule.RuleCode ?? "").Trim().ToUpperInvariant();
                if (code is not ("B04" or "B06"))
                    continue;

                List<ChallengeRuleItem> items = [];
                foreach (ChallengeRuleItem item in rule.RuleItemList)
                {
                    if (code == "B04")
                    {
                        string name = EntityText.RepairPerson(item.PersonName);
                        if (!EntityPostProcessor.IsAcceptablePerson(name))
                            continue;
                        List<string> origins = SanitizeOrigins(item.OriginText, pageText, name);
                        int count = item.Count > 0 ? item.Count : Math.Max(1, origins.Count);
                        items.Add(new ChallengeRuleItem
                        {
                            PersonName = name,
                            Count = count,
                            OriginText = origins,
                        });
                    }
                    else
                    {
                        string name = EntityText.Repair(item.CompanyName);
                        if (!EntityPostProcessor.IsAcceptableCompany(name))
                            continue;
                        List<string> origins = SanitizeOrigins(item.OriginText, pageText, name);
                        int count = item.Count > 0 ? item.Count : Math.Max(1, origins.Count);
                        items.Add(new ChallengeRuleItem
                        {
                            CompanyName = name,
                            Count = count,
                            OriginText = origins,
                        });
                    }
                }

                if (items.Count == 0)
                    continue;

                cleaned.Add(new ChallengeRule
                {
                    RuleCode = code,
                    RuleName = code == "B04" ? "人员名称" : "公司名称",
                    RuleItemList = items,
                });
            }

            return RefineAgainstPage(cleaned.Count > 0 ? cleaned : null, pageText);
        }

        // Fallback: companies/persons arrays → build via ChallengeResultMapper helpers
        List<string> persons = [];
        List<string> companies = [];
        if (payload.Persons is not null)
        {
            foreach (string p in payload.Persons)
            {
                string n = EntityText.RepairPerson(p);
                if (EntityPostProcessor.IsAcceptablePerson(n))
                    persons.Add(n);
            }
        }

        if (payload.Companies is not null)
        {
            foreach (string c in payload.Companies)
            {
                string n = EntityText.Repair(c);
                if (EntityPostProcessor.IsAcceptableCompany(n))
                    companies.Add(n);
            }
        }

        if (persons.Count == 0 && companies.Count == 0)
            return null;

        ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(
            "tmp",
            [new OcrPageResult { Page = 1, Text = pageText }],
            companies,
            persons);
        List<ChallengeRule>? fromArrays = mapped.Pages.Count > 0 && mapped.Pages[0].RuleList.Count > 0
            ? mapped.Pages[0].RuleList
            : null;
        return RefineAgainstPage(fromArrays, pageText);
    }

    /// <summary>
    /// When the page text is present, keep only names that survive the same
    /// alignment and filters as text NER, and rebuild count / originText from the text.
    /// </summary>
    private static List<ChallengeRule>? RefineAgainstPage(List<ChallengeRule>? rules, string pageText)
    {
        if (rules is null || rules.Count == 0 || string.IsNullOrWhiteSpace(pageText))
            return rules;

        List<string> persons = [];
        List<string> companies = [];
        foreach (ChallengeRule rule in rules)
        {
            if (rule.RuleItemList is null)
                continue;
            foreach (ChallengeRuleItem item in rule.RuleItemList)
            {
                if (rule.RuleCode == "B04" && !string.IsNullOrWhiteSpace(item.PersonName))
                    persons.Add(item.PersonName);
                else if (rule.RuleCode == "B06" && !string.IsNullOrWhiteSpace(item.CompanyName))
                    companies.Add(item.CompanyName);
            }
        }

        OcrEntities entities = EntityPostProcessor.Merge(
            [new OcrPageResult { Page = 1, Text = pageText }],
            companies,
            persons);
        ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(
            "tmp",
            [new OcrPageResult { Page = 1, Text = pageText }],
            entities.Companies.Select(c => c.Name).ToList(),
            entities.Persons.Select(p => p.Name).ToList());
        return mapped.Pages.Count > 0 && mapped.Pages[0].RuleList.Count > 0
            ? mapped.Pages[0].RuleList
            : null;
    }

    private static List<string> SanitizeOrigins(List<string>? raw, string pageText, string name)
    {
        List<string> result = [];
        if (raw is not null)
        {
            foreach (string o in raw)
            {
                if (string.IsNullOrWhiteSpace(o))
                    continue;
                string s = o.Trim();
                if (s.Length > ChallengeResultMapper.OriginMaxLen)
                    s = s[..ChallengeResultMapper.OriginMaxLen];
                if (s.Length >= ChallengeResultMapper.OriginMinLen)
                    result.Add(s);
                else if (s.Length > 0 && pageText.Length >= ChallengeResultMapper.OriginMinLen)
                {
                    // too short — try rebuild from page text
                    List<string> rebuilt = ChallengeResultMapper.BuildOriginTexts(pageText, name);
                    if (rebuilt.Count > 0)
                        return rebuilt;
                    result.Add(s);
                }
                else if (s.Length > 0)
                {
                    result.Add(s);
                }
            }
        }

        if (result.Count == 0 && !string.IsNullOrEmpty(pageText) && !string.IsNullOrEmpty(name))
            return ChallengeResultMapper.BuildOriginTexts(pageText, name);

        return result;
    }

}
