using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace LaserficheReports.Web;

internal enum QueryKind { LASERFICHE_QUERY, OCR_QUERY, HYBRID_QUERY }
internal sealed record QueryPlan(string Operation, string? Field = null, string? Value = null,
    string? Template = null, int? FolderId = null, string? Name = null, int Limit = 50,
    bool Content = false, string? GroupBy = null, string? From = null, string? To = null)
{
    public QueryKind Kind => Content ? Operation == "content" ? QueryKind.OCR_QUERY : QueryKind.HYBRID_QUERY
        : QueryKind.LASERFICHE_QUERY;
}

internal sealed class QuestionRouter(IHttpClientFactory clients)
{
    internal static QueryPlan? TryRoute(string question)
    {
        var q = HybridRetrieval.Normalize(question.Trim());
        var ids = ReportSupport.RequestedEntries(question);
        var content = Regex.IsMatch(q, @"(?:محتوي|داخل|نص|مكتوب|تتحدث|تتكلم|content|summarize|text)") ||
            (Regex.IsMatch(q, @"(?:لخص|تلخيص|حلل|تحليل)") && ids.Length > 0);
        if (ReportSupport.NeedsFilterClarification(question))
            return new QueryPlan("clarify");
        var condition = ReportSupport.ParseCondition(question);
        if (condition is not null)
            return new QueryPlan("search", condition.FieldQuestion, condition.ExpectedValue, Content: content);
        if (Regex.IsMatch(q, @"(?:حسب|توزيع|رتب).*?(?:الادار|الحال|القالب|القوالب)"))
            return new QueryPlan("group", GroupBy: q.Contains("ادار") ? "الإدارة" : q.Contains("حال") ? "إجراء الوثيقة" : "template");
        // An implicit status is not guessed: the exact field/value must be resolved below.
        var status = Regex.Match(q, @"(?:تحت (?:ال)?اجراء|المكتملة)");
        if (ids.Length == 0 && status.Value == "المكتملة") return new QueryPlan("clarify");
        if (ids.Length == 0 && status.Success)
        {
            // Normalize field names for discovery, but preserve the user's field
            // value: the repository decides which stored values match it.
            var value = Regex.Match(question, @"تحت\s+(?:ال)?[اأإآ]جراء").Value;
            return new QueryPlan("search", "إجراء الوثيقة", value, Content: content);
        }
        if (content && (ids.Length > 0 || Regex.IsMatch(q, @"(?:محتوي|نص).*?(?:الوثائق|المستندات|المستودع)")))
            return new QueryPlan("content", Content: true);
        if (ids.Length > 0 && Regex.IsMatch(q, @"(?:metadata|ميتا|بيانات|حقول|اسم|اين|مكان|مسار)"))
            return new QueryPlan("metadata");
        if (Regex.IsMatch(q, @"^(?:ما |ماهي |ما هي |اعرض |اذكر )?(?:القوالب|قوالب)(?: الموجودة| المتاحة)?[؟?]?$"))
            return new QueryPlan("templates");
        if (Regex.IsMatch(q, @"(?:اخر|آخر|recent).*?(?:معدل|تعديل|modified)"))
        {
            var match = Regex.Match(q, @"\d+");
            return new QueryPlan("recent", Limit: match.Success && int.TryParse(match.Value, out var n) ? n : 10);
        }
        if (q.Contains("اليوم") && Regex.IsMatch(q, @"(?:انشا|منشا|created)"))
            return new QueryPlan("created", From: "today", To: "today");
        if (ReportSupport.IsInventoryQuestion(question) || Regex.IsMatch(q, @"^كم (?:عدد )?(?:الوثائق|وثيقة|المستندات|المجلدات)[؟?]?$"))
            return new QueryPlan(q.Contains("مجلد") ? "folders" : "search");
        return null;
    }

    public async Task<QueryPlan> RouteAsync(string question, CancellationToken cancellationToken)
    {
        var fast = TryRoute(question);
        if (fast is not null) return fast;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await clients.CreateClient("ReportsGraph").PostAsJsonAsync("route",
            new { question }, budget.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<QueryPlan>(budget.Token)
            ?? throw new InvalidOperationException("The intent router returned no plan.");
    }
}
