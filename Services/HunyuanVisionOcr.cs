using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Per-page OCR against a locally hosted HunyuanOCR OpenAI-compatible server
/// (vLLM or llama.cpp <c>llama-server</c>). Not an in-process engine: there is
/// no official ONNX graph, and the supported GGUF path is <c>llama-server</c>,
/// which Native AOT cannot host inside this process. The prompt is HunyuanOCR's
/// document-parsing prompt, not the contest JSON schema.
/// AOT-safe: HttpClient + source-generated JSON. Never logs apiKey.
/// </summary>
public sealed class HunyuanVisionOcr
{
    private readonly HttpClient _http;
    private readonly HunyuanRuntimeConfig _config;
    private readonly ILogger<HunyuanVisionOcr> _logger;
    private readonly string _completionsUrl;

    public HunyuanVisionOcr(
        HttpClient http,
        HunyuanRuntimeConfig config,
        ILogger<HunyuanVisionOcr> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 10, 600));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        _completionsUrl = HunyuanRuntimeConfig.ChatCompletionsUrl(config.BaseUrl);
    }

    public HunyuanRuntimeConfig Config => _config;
    public bool IsUsable => _config.IsUsable;
    public int Concurrency => Math.Clamp(_config.Concurrency, 1, 64);
    public int JpegQuality => Math.Clamp(_config.JpegQuality, 40, 95);

    public async Task<OcrPageResult> RecognizePageAsync(
        int pageNumber,
        int width,
        int height,
        byte[] jpegBytes,
        double rasterMs,
        CancellationToken ct)
    {
        if (!IsUsable)
            throw new InvalidOperationException(
                "Hunyuan OCR is not configured (need enabled + baseUrl + model).");

        Stopwatch sw = Stopwatch.StartNew();
        string dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);
        string text = await CompleteAsync(pageNumber, dataUrl, ct).ConfigureAwait(false);
        sw.Stop();

        if (text.Length > 100_000)
        {
            _logger.LogWarning(
                "Hunyuan OCR page {Page} text truncated from {Length} to 100000 chars",
                pageNumber,
                text.Length);
            text = text[..100_000];
        }

        return new OcrPageResult
        {
            Page = pageNumber,
            Width = width,
            Height = height,
            Text = text,
            RasterizeMs = Math.Round(rasterMs, 1),
            OcrMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
        };
    }

    private async Task<string> CompleteAsync(int pageNumber, string imageDataUrl, CancellationToken ct)
    {
        HunyuanChatCompletionRequest body = new()
        {
            Model = _config.Model,
            Temperature = 0,
            MaxTokens = _config.MaxTokens,
            Messages =
            [
                new HunyuanChatMessage
                {
                    Role = "user",
                    Content =
                    [
                        new HunyuanContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new HunyuanImageUrl { Url = imageDataUrl },
                        },
                        new HunyuanContentPart { Type = "text", Text = _config.Prompt },
                    ],
                },
            ],
        };

        _logger.LogInformation(
            "Hunyuan OCR request: page={Page}, model={Model}, maxTokens={MaxTokens}, url={Url}",
            pageNumber,
            _config.Model,
            body.MaxTokens,
            _completionsUrl);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsJsonAsync(_completionsUrl, body, AppJsonContext.Default.HunyuanChatCompletionRequest, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                $"Hunyuan OCR request failed ({_completionsUrl}): {ex.Message}", ex);
        }

        using (response)
        {
            string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
                throw new HttpRequestException(
                    $"Hunyuan OCR failed HTTP {(int)response.StatusCode}: {snippet}");
            }

            ChatCompletionResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(raw, AppJsonContext.Default.ChatCompletionResponse);
            }
            catch (JsonException ex)
            {
                string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
                throw new InvalidOperationException(
                    "Hunyuan OCR response was not an OpenAI chat completion: " + snippet, ex);
            }

            string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Hunyuan OCR returned empty message content.");

            return content.Replace("\r", "").Trim();
        }
    }
}
