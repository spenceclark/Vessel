using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>
/// #113 — TypeSafe System One (Jev): <c>POST /v1/systemone</c> direct, or OpenRouter's
/// <c>/api/alpha/decisions</c>. Not a chat shape: a <c>state</c> plus a map of typed
/// <c>questions</c> in, a map of typed <c>answers</c> out under the same keys. The response
/// is a single JSON document — the API has no streaming and no stop reason.
/// </summary>
public sealed class TypeSafeSystemOneAdapter : IFormatAdapter
{
    public AdapterResult Parse(AdapterInput input)
    {
        JsonObject? doc = JsonUtil.Object(JsonUtil.Parse(input.ResponseText));
        JsonObject? usage = JsonUtil.Object(doc?["usage"]);

        return new AdapterResult
        {
            // The response names the dated build an alias like `jev-latest` resolved to.
            Model = JsonUtil.Str(doc?["model"]) ?? JsonUtil.Str(JsonUtil.Object(input.Request)?["model"]),
            TokensIn = JsonUtil.Long(usage?["input_tokens"]),
            TokensOut = JsonUtil.Long(usage?["output_tokens"]),
            PromptText = TextFlattener.SystemOneQuestions(input.Request),
            ResponseText = TextFlattener.SystemOneAnswers(doc),
        };
    }
}
