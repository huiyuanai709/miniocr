using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// OpenAI-compatible Chat Completions NER for companies/persons.
/// POST {baseUrl}/v1/chat/completions — works with OpenAI, DeepSeek, Azure-compatible, local.
/// AOT-safe: HttpClient + source-generated JSON. Never logs apiKey.
/// </summary>
public sealed class LlmEntityExtractor
{
    private const string SystemPrompt = NerPrompt.Text;
    private const string SystemPromptPages = NerPrompt.TextPages;

    private readonly HttpClient _http;
    private readonly LlmRuntimeConfig _config;
    private readonly ILogger<LlmEntityExtractor> _logger;
    private int _jsonObjectEnabled = 1;

    public LlmEntityExtractor(
        HttpClient http,
        LlmRuntimeConfig config,
        ILogger<LlmEntityExtractor> logger)
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

    /// <summary>
    /// Start a streaming NER session. Call <see cref="LlmExtractionSession.Add"/>
    /// as each page finishes OCR (any order); groups of non-empty pages are
    /// sent under <see cref="LlmRuntimeConfig.MaxConcurrency"/> without waiting
    /// for the rest of the document. Then <see cref="LlmExtractionSession.CompleteAsync"/>.
    /// </summary>
    public LlmExtractionSession Begin(int pageCount, CancellationToken ct)
    {
        if (!IsUsable)
            throw new InvalidOperationException("LLM entity extraction is not configured.");

        int concurrency = Math.Clamp(_config.MaxConcurrency, 1, 32);
        _logger.LogInformation(
            "LLM NER streaming: pageCount={PageCount}, pagesPerRequest={PagesPerRequest}, maxCharsPerRequest={MaxChars}, maxConcurrency={MaxConcurrency}",
            pageCount,
            _config.PagesPerRequest,
            _config.MaxCharsPerRequest,
            concurrency);
        return new LlmExtractionSession(this, pageCount, concurrency, ct);
    }

    public Task<LlmExtractionResult> ExtractAsync(
        IReadOnlyList<OcrPageResult> pages,
        CancellationToken ct)
    {
        if (!IsUsable)
            throw new InvalidOperationException("LLM entity extraction is not configured.");

        int pageCount = 0;
        foreach (OcrPageResult page in pages)
            pageCount = Math.Max(pageCount, page.Page);

        LlmExtractionSession session = Begin(pageCount, ct);
        foreach (OcrPageResult page in pages)
            session.Add(page);
        return session.CompleteAsync();
    }

    public async Task<LlmEntityPayload> ExtractBatchAsync(
        string userText,
        IReadOnlyList<int> pageNumbers,
        CancellationToken ct)
    {
        BatchResult result = await CompleteBatchAsync(
            new LlmPageGrouper.PageBatch(userText, pageNumbers as int[] ?? pageNumbers.ToArray()),
            pagesMode: false,
            ct).ConfigureAwait(false);
        return new LlmEntityPayload { Companies = result.Companies, Persons = result.Persons };
    }

    /// <summary>Per-page batch call used by the distributed NER path (direction A).</summary>
    internal async Task<List<ChallengePageResult>> ExtractPagesBatchAsync(
        string userText,
        IReadOnlyList<int> pageNumbers,
        CancellationToken ct)
    {
        BatchResult result = await CompleteBatchAsync(
            new LlmPageGrouper.PageBatch(userText, pageNumbers as int[] ?? pageNumbers.ToArray()),
            pagesMode: true,
            ct).ConfigureAwait(false);
        return result.Pages;
    }

    public async Task<ClusterNerResultRequest> ExtractDistributedAsync(
        ClusterNerAssignment claim,
        CancellationToken ct)
    {
        string prompt = ClusterNerPrompt.WithLookahead(claim.PromptText, claim.Lookahead, claim.LookaheadPage);
        List<ChallengePageResult> modelPages = await ExtractPagesBatchAsync(prompt, claim.Pages, ct)
            .ConfigureAwait(false);
        List<ClusterNerEntityHit> hits = ClusterNerAssembler.BuildFromPages(claim.Bodies, modelPages);

        List<string> companies = [];
        List<string> persons = [];
        foreach (ClusterNerEntityHit hit in hits)
        {
            if (hit.Kind == "company")
                companies.Add(hit.Name);
            else if (hit.Kind == "person")
                persons.Add(hit.Name);
        }

        return new ClusterNerResultRequest
        {
            Companies = companies,
            Persons = persons,
            Entities = hits,
        };
    }

    /// <summary>
    /// Overlaps LLM NER with OCR. Not thread-safe for <see cref="CompleteAsync"/>
    /// racing <see cref="Add"/>; OCR workers may call <see cref="Add"/> concurrently.
    /// A full group is held until the next non-empty page is known, so the request
    /// can include that page's first 240 characters, or until <see cref="CompleteAsync"/>.
    /// </summary>
    public sealed class LlmExtractionSession
    {
        private readonly LlmEntityExtractor _owner;
        private readonly LlmPageGrouper.OrderedBuffer _buffer;
        private readonly SemaphoreSlim _slots;
        private readonly CancellationToken _ct;
        private readonly CancellationTokenSource _failCts;
        private readonly object _gate = new();
        private readonly List<Task> _tasks = [];
        private readonly List<string> _companies = [];
        private readonly List<string> _persons = [];
        private readonly Dictionary<int, ChallengePageResult> _pageRules = [];
        private LlmPageGrouper.PageBatch? _held;
        private long _requestTicks;
        private long _firstStart;
        private long _lastEnd;
        private int _groups;
        private int _inFlight;
        private int _peak;

        /// <summary>Filled when <see cref="CompleteAsync"/> finishes, including the failure path.</summary>
        internal NerStages Stages { get; private set; }

        internal LlmExtractionSession(
            LlmEntityExtractor owner,
            int pageCount,
            int concurrency,
            CancellationToken ct)
        {
            _owner = owner;
            _ct = ct;
            _failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _slots = new SemaphoreSlim(concurrency, concurrency);
            _buffer = new LlmPageGrouper.OrderedBuffer(
                pageCount,
                owner._config.PagesPerRequest,
                owner._config.MaxCharsPerRequest);
        }

        public void Add(OcrPageResult page)
        {
            List<LlmPageGrouper.PageBatch> ready;
            lock (_gate)
                ready = CollectSendable(_buffer.Add(page), flushHeld: false);
            foreach (LlmPageGrouper.PageBatch batch in ready)
                Queue(batch);
        }

        public async Task<LlmExtractionResult> CompleteAsync()
        {
            List<LlmPageGrouper.PageBatch> tail;
            OcrPageResult[] pages;
            lock (_gate)
            {
                tail = CollectSendable(_buffer.FlushRemainder(), flushHeld: true);
                pages = _buffer.NonEmptyPages.ToArray();
            }

            foreach (LlmPageGrouper.PageBatch batch in tail)
                Queue(batch);

            Task[] pending;
            lock (_gate)
                pending = _tasks.ToArray();

            try
            {
                if (pending.Length == 0)
                {
                    _owner._logger.LogInformation(
                        "LLM NER: no non-empty pages; skipping chat completions");
                    return BuildResult(pages);
                }

                await Task.WhenAll(pending).ConfigureAwait(false);
                return BuildResult(pages);
            }
            finally
            {
                Stages = SnapshotStages();
                if (Stages.Groups > 0)
                {
                    StageLog.NerSummary(
                        _owner._logger,
                        Stages.Groups,
                        Math.Round(Stages.WallMs, 1),
                        Math.Round(Stages.RequestMs, 1),
                        Stages.Peak);
                }

                _slots.Dispose();
                _failCts.Dispose();
            }
        }

        /// <summary>
        /// Model-side per-page rules win when present; otherwise fall back to the
        /// flat name lists merged against the pages.
        /// </summary>
        private LlmExtractionResult BuildResult(OcrPageResult[] pages)
        {
            List<ChallengePageResult> modelPages;
            OcrEntities entities;
            lock (_gate)
            {
                modelPages = _pageRules.Values.OrderBy(p => p.Page).ToList();
                entities = modelPages.Count > 0
                    ? new OcrEntities()
                    : MergeToEntities(pages, _companies, _persons);
            }

            return new LlmExtractionResult { Entities = entities, Pages = modelPages };
        }

        private NerStages SnapshotStages()
        {
            long first = Volatile.Read(ref _firstStart);
            long last = Volatile.Read(ref _lastEnd);
            double wall = first == 0 || last < first ? 0 : StageClock.TicksToMs(last - first);
            return new NerStages(
                wall,
                StageClock.TicksToMs(Volatile.Read(ref _requestTicks)),
                Volatile.Read(ref _groups),
                Volatile.Read(ref _peak));
        }

        /// <summary>Observe in-flight calls after a failure. Safe to call once.</summary>
        public async Task AbandonAsync()
        {
            try
            {
                _failCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CompleteAsync already finished.
            }

            Task[] pending;
            lock (_gate)
                pending = _tasks.ToArray();
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch
            {
                // Caller is already on a failure path.
            }
        }

        /// <summary>
        /// Groups already emitted stay unsent until the next non-empty page can supply
        /// a 240-character head. <paramref name="flushHeld"/> sends the trailing group
        /// with no further lookahead; that is the end of the document.
        /// </summary>
        private List<LlmPageGrouper.PageBatch> CollectSendable(
            List<LlmPageGrouper.PageBatch> emitted,
            bool flushHeld)
        {
            List<LlmPageGrouper.PageBatch> send = [];
            foreach (LlmPageGrouper.PageBatch batch in emitted)
            {
                if (_held is { } previous)
                    send.Add(WithLookahead(previous));
                _held = batch;
            }

            if (_held is { } waiting && (flushHeld || HasLaterNonEmpty(waiting)))
            {
                send.Add(WithLookahead(waiting));
                _held = null;
            }

            return send;
        }

        private bool HasLaterNonEmpty(LlmPageGrouper.PageBatch batch)
        {
            int last = batch.PageNumbers.Length == 0 ? 0 : batch.PageNumbers[^1];
            foreach (OcrPageResult known in _buffer.NonEmptyPages)
            {
                if (known.Page > last && !string.IsNullOrEmpty(ClusterNerPrompt.Head(known.Text)))
                    return true;
            }

            return false;
        }

        private LlmPageGrouper.PageBatch WithLookahead(LlmPageGrouper.PageBatch batch)
        {
            int last = batch.PageNumbers.Length == 0 ? 0 : batch.PageNumbers[^1];
            foreach (OcrPageResult known in _buffer.NonEmptyPages)
            {
                if (known.Page <= last)
                    continue;
                string head = ClusterNerPrompt.Head(known.Text);
                if (head.Length == 0)
                    continue;
                return new LlmPageGrouper.PageBatch(
                    ClusterNerPrompt.WithLookahead(batch.Text, head, known.Page),
                    batch.PageNumbers);
            }

            return batch;
        }

        private void Queue(LlmPageGrouper.PageBatch batch)
        {
            lock (_gate)
                _tasks.Add(RunAsync(batch));
        }

        private async Task RunAsync(LlmPageGrouper.PageBatch batch)
        {
            bool acquired = false;
            bool started = false;
            long start = 0;
            try
            {
                await _slots.WaitAsync(_failCts.Token).ConfigureAwait(false);
                acquired = true;
                int now = Interlocked.Increment(ref _inFlight);
                StageClock.RaisePeak(ref _peak, now);
                start = StageClock.Stamp();
                started = true;
                BatchResult payload = await _owner.CompleteBatchAsync(batch, pagesMode: true, _failCts.Token)
                    .ConfigureAwait(false);

                List<string> localCompanies = [];
                List<string> localPersons = [];
                foreach (string c in payload.Companies)
                {
                    if (!string.IsNullOrWhiteSpace(c))
                        localCompanies.Add(c);
                }

                foreach (string person in payload.Persons)
                {
                    if (!string.IsNullOrWhiteSpace(person))
                        localPersons.Add(person);
                }

                if (payload.Pages.Count == 0 && localCompanies.Count == 0 && localPersons.Count == 0)
                    return;

                lock (_gate)
                {
                    foreach (ChallengePageResult page in payload.Pages)
                        _pageRules[page.Page] = page;
                    _companies.AddRange(localCompanies);
                    _persons.AddRange(localPersons);
                }
            }
            catch
            {
                try
                {
                    _failCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                throw;
            }
            finally
            {
                if (started)
                {
                    long end = StageClock.Stamp();
                    long elapsed = end - start;
                    Interlocked.Add(ref _requestTicks, elapsed);
                    StageClock.NoteMin(ref _firstStart, start);
                    StageClock.NoteMax(ref _lastEnd, end);
                    Interlocked.Increment(ref _groups);
                    Interlocked.Decrement(ref _inFlight);
                    if (_owner._logger.IsEnabled(LogLevel.Debug))
                    {
                        StageLog.NerGroup(
                            _owner._logger,
                            string.Join(',', batch.PageNumbers),
                            Math.Round(StageClock.TicksToMs(elapsed), 1));
                    }
                }

                if (acquired)
                    _slots.Release();
            }
        }
    }

    private async Task<BatchResult> CompleteBatchAsync(
        LlmPageGrouper.PageBatch batch,
        bool pagesMode,
        CancellationToken ct)
    {
        Exception? parseError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await CompleteBatchOnceAsync(batch, pagesMode, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && LlmJson.IsParseFailure(ex) && !ct.IsCancellationRequested)
            {
                parseError = ex;
                _logger.LogWarning(
                    ex,
                    "LLM NER JSON parse failed for pages {Pages}; retrying the group once",
                    string.Join(',', batch.PageNumbers));
            }
        }

        throw parseError ?? new InvalidOperationException("LLM NER JSON parse failed.");
    }

    private async Task<BatchResult> CompleteBatchOnceAsync(
        LlmPageGrouper.PageBatch batch,
        bool pagesMode,
        CancellationToken ct)
    {
        string userText = batch.Text;
        string url = _config.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
        bool jsonMode = _config.JsonObject && Volatile.Read(ref _jsonObjectEnabled) == 1;
        ChatCompletionRequest body = new()
        {
            Model = _config.Model,
            Temperature = 0,
            Thinking = _config.ToThinkingOption(),
            ResponseFormat = jsonMode ? LlmJson.JsonObject : null,
            Messages =
            [
                new ChatMessage { Role = "system", Content = pagesMode ? SystemPromptPages : SystemPrompt },
                new ChatMessage { Role = "user", Content = userText },
            ],
        };

        _logger.LogInformation(
            "LLM NER request: model={Model}, chars={Chars}, pages={Pages}, thinking={Thinking}, jsonObject={JsonObject}, url={Url}",
            _config.Model,
            userText.Length,
            string.Join(',', batch.PageNumbers),
            body.Thinking?.Type ?? "(null)",
            jsonMode,
            url);

        string raw = await PostChatAsync(url, body, jsonMode, ct).ConfigureAwait(false);
        ChatCompletionResponse? parsed =
            JsonSerializer.Deserialize(raw, AppJsonContext.Default.ChatCompletionResponse);
        string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("LLM returned empty message content.");

        string json = NerPrompt.ExtractJsonObject(content);
        BatchResult result = new();

        // Per-page payload (direction A) wins; a flat name list is the fallback shape.
        LlmPagesPayload? pages = LlmJson.Deserialize<LlmPagesPayload>(json);
        if (pages?.Pages is { Count: > 0 })
        {
            result.Pages.AddRange(SanitizePages(pages.Pages));
            return result;
        }

        LlmEntityPayload? payload = LlmJson.Deserialize<LlmEntityPayload>(json);
        if (payload?.Companies is not null)
        {
            foreach (string c in payload.Companies)
            {
                if (!string.IsNullOrWhiteSpace(c))
                    result.Companies.Add(c);
            }
        }

        if (payload?.Persons is not null)
        {
            foreach (string p in payload.Persons)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    result.Persons.Add(p);
            }
        }

        return result;
    }

    /// <summary>Model-produced per-page rules: keep B04/B06 only, repair names, drop empties.</summary>
    internal static List<ChallengePageResult> SanitizePages(IEnumerable<LlmPageEntities> raw)
    {
        List<ChallengePageResult> pages = [];
        foreach (LlmPageEntities page in raw)
        {
            if (page.Page <= 0 || page.RuleList is null)
                continue;
            List<ChallengeRule> rules = SanitizeRules(page.RuleList);
            if (rules.Count == 0)
                continue;
            pages.Add(new ChallengePageResult { Page = page.Page, RuleList = rules });
        }

        return pages;
    }

    private static List<ChallengeRule> SanitizeRules(IEnumerable<ChallengeRule> raw)
    {
        List<ChallengeRule> rules = [];
        foreach (ChallengeRule rule in raw)
        {
            string code = (rule.RuleCode ?? "").Trim().ToUpperInvariant();
            if (code is not ("B04" or "B06") || rule.RuleItemList is null)
                continue;
            bool person = code == "B04";
            List<ChallengeRuleItem> items = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ChallengeRuleItem item in rule.RuleItemList)
            {
                string name = person
                    ? EntityText.RepairPerson(item.PersonName)
                    : EntityText.Repair(item.CompanyName);
                if (name.Length == 0 || !seen.Add(EntityText.Identity(name)))
                    continue;

                List<string> origins = [];
                if (item.OriginText is not null)
                {
                    foreach (string origin in item.OriginText)
                    {
                        if (string.IsNullOrWhiteSpace(origin))
                            continue;
                        string s = origin.Trim();
                        origins.Add(s.Length > ChallengeResultMapper.OriginMaxLen
                            ? s[..ChallengeResultMapper.OriginMaxLen]
                            : s);
                    }
                }

                items.Add(new ChallengeRuleItem
                {
                    PersonName = person ? name : null,
                    CompanyName = person ? null : name,
                    Count = item.Count > 0 ? item.Count : Math.Max(1, origins.Count),
                    OriginText = origins,
                });
            }

            if (items.Count == 0)
                continue;

            rules.Add(new ChallengeRule
            {
                RuleCode = code,
                RuleName = person ? "人员名称" : "公司名称",
                RuleItemList = items,
            });
        }

        return rules;
    }

    /// <summary>One batch's model output before it is folded into the session result.</summary>
    private sealed class BatchResult
    {
        public List<ChallengePageResult> Pages { get; } = [];
        public List<string> Companies { get; } = [];
        public List<string> Persons { get; } = [];
    }

    private async Task<string> PostChatAsync(
        string url,
        ChatCompletionRequest body,
        bool jsonMode,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(url, body, AppJsonContext.Default.ChatCompletionRequest, ct)
            .ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            && jsonMode
            && LlmJson.IsFormatRejected((int)response.StatusCode, raw))
        {
            Volatile.Write(ref _jsonObjectEnabled, 0);
            _logger.LogWarning(
                "LLM rejected response_format=json_object; disabling it and retrying without the field");
            body.ResponseFormat = null;
            using HttpResponseMessage retry = await _http
                .PostAsJsonAsync(url, body, AppJsonContext.Default.ChatCompletionRequest, ct)
                .ConfigureAwait(false);
            raw = await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!retry.IsSuccessStatusCode)
            {
                string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
                throw new HttpRequestException(
                    $"LLM chat completions failed HTTP {(int)retry.StatusCode}: {snippet}");
            }

            return raw;
        }

        if (!response.IsSuccessStatusCode)
        {
            string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
            throw new HttpRequestException(
                $"LLM chat completions failed HTTP {(int)response.StatusCode}: {snippet}");
        }

        return raw;
    }

    internal static OcrEntities MergeToEntities(
        IReadOnlyList<OcrPageResult> pages,
        IEnumerable<string> companies,
        IEnumerable<string> persons) =>
        EntityPostProcessor.Merge(pages, companies, persons);
}
