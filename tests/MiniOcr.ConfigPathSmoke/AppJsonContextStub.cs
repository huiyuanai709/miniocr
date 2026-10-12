using System.Text.Json.Serialization;
using MiniOcr.Models;

namespace MiniOcr;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfigFile))]
[JsonSerializable(typeof(LlmFileConfig))]
[JsonSerializable(typeof(OcrFileConfig))]
[JsonSerializable(typeof(ChallengeFileConfig))]
internal partial class AppJsonContext : JsonSerializerContext;
