using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIMediaWorker.Llm;

internal static class LlmJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
