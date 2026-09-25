using System.Text.Json.Serialization;
using MiniOcr.Models;

namespace MiniOcr;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfigFile))]
[JsonSerializable(typeof(LlmFileConfig))]
[JsonSerializable(typeof(OcrFileConfig))]
[JsonSerializable(typeof(HunyuanFileConfig))]
[JsonSerializable(typeof(HunyuanChatCompletionRequest))]
[JsonSerializable(typeof(HunyuanChatMessage))]
[JsonSerializable(typeof(HunyuanContentPart))]
[JsonSerializable(typeof(HunyuanImageUrl))]
[JsonSerializable(typeof(List<HunyuanChatMessage>))]
[JsonSerializable(typeof(List<HunyuanContentPart>))]
internal partial class AppJsonContext : JsonSerializerContext;
