using System.Text.Json;
using MiniOcr;
using MiniOcr.Models;
using MiniOcr.Services;

int failed = 0;

void Assert(bool cond, string msg)
{
    Console.WriteLine(cond ? "  PASS  " + msg : "  FAIL  " + msg);
    if (!cond)
        failed++;
}

string trailing = """{"companies":["甲公司",],"persons":["张伟",]}""";
LlmEntityPayload? entity = LlmJson.Deserialize<LlmEntityPayload>(trailing);
Assert(entity?.Companies is ["甲公司"] && entity.Persons is ["张伟"], "source-gen parse allows trailing commas");

string commented = """{ /*note*/ "companies": [], "persons": ["李娜"] }""";
LlmEntityPayload? commentedPayload = LlmJson.Deserialize<LlmEntityPayload>(commented);
Assert(commentedPayload?.Persons is ["李娜"], "source-gen parse skips comments");

string fenced = NerPrompt.ExtractJsonObject("```json\n{\"companies\":[\"A\"],\"persons\":[]}\n```");
LlmEntityPayload? fromFence = LlmJson.Deserialize<LlmEntityPayload>(fenced);
Assert(fromFence?.Companies is ["A"], "fence strip then source-gen parse");

var request = new ChatCompletionRequest
{
    Model = "deepseek-chat",
    Temperature = 0,
    ResponseFormat = LlmJson.JsonObject,
    Messages = [new ChatMessage { Role = "user", Content = "return json" }],
};
string wire = JsonSerializer.Serialize(request, AppJsonContext.Default.ChatCompletionRequest);
Assert(wire.Contains("\"response_format\"", StringComparison.Ordinal), "wire field is response_format");
Assert(wire.Contains("\"json_object\"", StringComparison.Ordinal), "wire type is json_object");
Assert(!wire.Contains("responseFormat", StringComparison.Ordinal), "camelCase name is not sent");

Assert(LlmJson.IsFormatRejected(400, "{\"error\":\"unsupported response_format\"}"), "400 mentioning response_format is a rejection");
Assert(!LlmJson.IsFormatRejected(500, "response_format"), "non-400 is not a format rejection");
Assert(!LlmJson.IsFormatRejected(400, "model not found"), "unrelated 400 stays a hard error");
Assert(LlmJson.IsParseFailure(new JsonException("bad")), "JsonException is a local retry");
Assert(!LlmJson.IsParseFailure(new HttpRequestException("timeout")), "transport errors are not parse retries");

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    return 1;
}

Console.WriteLine("ALL PASSED");
return 0;
