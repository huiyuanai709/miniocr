using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniOcr;

public partial class AppJsonContext
{
    /// <summary>
    /// Same camelCase contract as <see cref="Default"/>, with non-ASCII left as UTF-8.
    /// Challenge callbacks and HTTP JSON responses use this so Chinese is not written as <c>\uXXXX</c>.
    /// </summary>
    public static AppJsonContext Relaxed { get; } = new(CreateRelaxedOptions());

    private static JsonSerializerOptions CreateRelaxedOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
