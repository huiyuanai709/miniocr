using System.Text.Json;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Shared chat-completions JSON mode. <c>response_format=json_object</c> is on unless the
/// provider rejects it. Payloads are read with the AOT source-gen context plus trailing
/// commas and comments, which some models still emit inside an otherwise strict object.
/// </summary>
public static class LlmJson
{
    private static readonly JsonSerializerOptions Lenient = CreateLenient();

    public static ChatResponseFormat JsonObject { get; } = new() { Type = "json_object" };

    public static bool IsFormatRejected(int statusCode, string body)
    {
        if (statusCode != 400 || string.IsNullOrEmpty(body))
            return false;
        return body.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || body.Contains("json_object", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsParseFailure(Exception ex) =>
        ex is JsonException or InvalidOperationException;

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Lenient);
    }

    private static JsonSerializerOptions CreateLenient()
    {
        JsonSerializerOptions options = new(AppJsonContext.Default.Options)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.TypeInfoResolver ??= AppJsonContext.Default;
        return options;
    }
}
