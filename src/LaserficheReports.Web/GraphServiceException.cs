using System.Net.Http.Json;
using System.Text.Json;

namespace LaserficheReports.Web;

internal sealed class GraphServiceException(string code, string stage, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
    public string Stage { get; } = stage;

    internal static string MessageFor(string code) => code switch
    {
        "graph_protocol_mismatch" => "خدمة تحليل التقارير تعمل بإصدار مختلف. أوقفها ثم أعد تشغيل tools/reports-graph/start.ps1 من النسخة المحدثة.",
        "ollama_unavailable" => "تعذر الاتصال بـ Ollama المحلي. شغّل Ollama ثم أعد الطلب؛ لم يتم تنفيذ بحث أو اختراع نتائج.",
        "model_not_found" => "نموذج التقارير غير موجود في Ollama. تحقق من اسم النموذج في أمر تشغيل خدمة التقارير ومن ollama list.",
        "local_model_timeout" => "استغرق النموذج أكثر من المهلة المحددة. زِد REPORTS_MODEL_TIMEOUT_SECONDS وأعد تشغيل خدمة التقارير.",
        "local_model_invalid_output" => "لم ينتج AI خطة أو إجابة صالحة بعد محاولة التصحيح. لم تُنفذ خطة غير موثوقة.",
        "local_model_busy" => "النموذج المحلي ما زال يعالج طلبًا آخر. أعد الطلب بعد اكتماله.",
        _ => "تعذر الاتصال بخدمة تحليل التقارير المحلية على المنفذ 8766. شغّل tools/reports-graph/start.ps1 وتحقق من سجلها."
    };

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string stage, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.StatusCode == System.Net.HttpStatusCode.NotFound ? "graph_protocol_mismatch" : "graph_unavailable";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (error.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var candidate = value.GetString();
                if (candidate is "ollama_unavailable" or "model_not_found" or "local_model_timeout" or
                    "local_model_invalid_output" or "local_model_busy") code = candidate;
            }
        }
        catch (JsonException) { }
        throw new GraphServiceException(code, stage, MessageFor(code));
    }
}
