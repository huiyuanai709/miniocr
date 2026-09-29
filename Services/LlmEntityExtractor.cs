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

    private readonly HttpClient _http;
    private readonly LlmRuntimeConfig _config;
    private readonly ILogger<LlmEntityExtractor> _logger;

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

    public Task<OcrEntities> ExtractAsync(
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

    public Task<LlmEntityPayload> ExtractBatchAsync(
        string userText,
        IReadOnlyList<int> pageNumbers,
        CancellationToken ct) =>
        CompleteBatchAsync(new LlmPageGrouper.PageBatch(userText, pageNumbers as int[] ?? pageNumbers.ToArray()), ct);

    public async Task<ClusterNerResultRequest> ExtractDistributedAsync(
        ClusterNerAssignment claim,
        CancellationToken ct)
    {
        string prompt = ClusterNerPrompt.WithLookahead(claim.PromptText, claim.Lookahead, claim.LookaheadPage);
        LlmEntityPayload payload = await ExtractBatchAsync(prompt, claim.Pages, ct).ConfigureAwait(false);
        return new ClusterNerResultRequest
        {
            Companies = payload.Companies,
            Persons = payload.Persons,
            Entities = ClusterNerAssembler.Build(claim.Bodies, claim.Lookahead, payload),
        };
    }

    /// <summary>
    /// Overlaps LLM NER with OCR. Not thread-safe for <see cref="CompleteAsync"/>
    /// racing <see cref="Add"/>; OCR workers may call <see cref="Add"/> concurrently.
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
                ready = _buffer.Add(page);
            foreach (LlmPageGrouper.PageBatch batch in ready)
                Queue(batch);
        }

        public async Task<OcrEntities> CompleteAsync()
        {
            List<LlmPageGrouper.PageBatch> tail;
            OcrPageResult[] pages;
            lock (_gate)
            {
                tail = _buffer.FlushRemainder();
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
                    return MergeToEntities(pages, _companies, _persons);
                }

                await Task.WhenAll(pending).ConfigureAwait(false);
                return MergeToEntities(pages, _companies, _persons);
            }
            finally
            {
                _slots.Dispose();
                _failCts.Dispose();
            }
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

        private void Queue(LlmPageGrouper.PageBatch batch)
        {
            lock (_gate)
                _tasks.Add(RunAsync(batch));
        }

        private async Task RunAsync(LlmPageGrouper.PageBatch batch)
        {
            bool acquired = false;
            try
            {
                await _slots.WaitAsync(_failCts.Token).ConfigureAwait(false);
                acquired = true;
                LlmEntityPayload payload = await _owner.CompleteBatchAsync(batch, _failCts.Token)
                    .ConfigureAwait(false);

                List<string> localCompanies = [];
                List<string> localPersons = [];
                if (payload.Companies is not null)
                {
                    foreach (string c in payload.Companies)
                    {
                        if (!string.IsNullOrWhiteSpace(c))
                            localCompanies.Add(c);
                    }
                }

                if (payload.Persons is not null)
                {
                    foreach (string person in payload.Persons)
                    {
                        if (!string.IsNullOrWhiteSpace(person))
                            localPersons.Add(person);
                    }
                }

                if (localCompanies.Count == 0 && localPersons.Count == 0)
                    return;

                lock (_gate)
                {
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
                if (acquired)
                    _slots.Release();
            }
        }
    }

    private async Task<LlmEntityPayload> CompleteBatchAsync(
        LlmPageGrouper.PageBatch batch,
        CancellationToken ct)
    {
        string userText = batch.Text;
        string url = _config.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
        ChatCompletionRequest body = new()
        {
            Model = _config.Model,
            Temperature = 0,
            Thinking = _config.ToThinkingOption(),
            Messages =
            [
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = userText },
            ],
        };

        _logger.LogInformation(
            "LLM NER request: model={Model}, chars={Chars}, pages={Pages}, thinking={Thinking}, url={Url}",
            _config.Model,
            userText.Length,
            string.Join(',', batch.PageNumbers),
            body.Thinking?.Type ?? "(null)",
            url);

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(url, body, AppJsonContext.Default.ChatCompletionRequest, ct)
            .ConfigureAwait(false);

        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
            throw new HttpRequestException(
                $"LLM chat completions failed HTTP {(int)response.StatusCode}: {snippet}");
        }

        ChatCompletionResponse? parsed =
            JsonSerializer.Deserialize(raw, AppJsonContext.Default.ChatCompletionResponse);
        string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("LLM returned empty message content.");

        string json = NerPrompt.ExtractJsonObject(content);
        LlmEntityPayload? payload =
            JsonSerializer.Deserialize(json, AppJsonContext.Default.LlmEntityPayload);
        return payload ?? new LlmEntityPayload();
    }

    internal static OcrEntities MergeToEntities(
        IReadOnlyList<OcrPageResult> pages,
        IEnumerable<string> companies,
        IEnumerable<string> persons) =>
        EntityPostProcessor.Merge(pages, companies, persons);
}
