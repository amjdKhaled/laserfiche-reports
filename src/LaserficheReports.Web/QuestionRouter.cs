using System.Net.Http.Json;

namespace LaserficheReports.Web;

internal enum QueryKind { LASERFICHE_QUERY, OCR_QUERY, HYBRID_QUERY }
internal sealed record QueryPlan(string Operation, string? Field = null, string? Value = null,
    string? Template = null, int? FolderId = null, string? Name = null, int Limit = 50,
    bool Content = false, string? GroupBy = null, string? From = null, string? To = null, string? Title = null, string? Sort = null, int[]? EntryIds = null, string? Question = null)
{
    public QueryKind Kind => Content ? Operation == "content" ? QueryKind.OCR_QUERY : QueryKind.HYBRID_QUERY
        : QueryKind.LASERFICHE_QUERY;
}

internal sealed class QuestionRouter(IHttpClientFactory clients)
{
    public async Task<ReportRequest> RouteAsync(string question, object catalog, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        using var response = await clients.CreateClient("ReportsGraph").PostAsJsonAsync("route",
            new { question, catalog, today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd") }, budget.Token);
        response.EnsureSuccessStatusCode();
        var request = await response.Content.ReadFromJsonAsync<ReportRequest>(budget.Token)
            ?? throw new InvalidOperationException("لم يرجع الذكاء الاصطناعي خطة للطلب.");
        if (request.Reports is null || request.Reports.Length is < 1 or > 6)
            throw new ArgumentException("اطلب من تقرير واحد إلى ستة تقارير في السؤال الواحد.");
        var explicitIds = ReportSupport.RequestedEntries(question);
        foreach (var plan in request.Reports)
        {
            if (plan.Operation is not ("search" or "folders" or "metadata" or "templates" or "recent" or "latest_created" or "latest_modified" or "created" or "modified" or "group" or "content" or "clarify") ||
                plan.Limit is < 1 or > 200 || plan.Sort is not (null or "creationTime desc" or "lastModifiedTime desc" or "id asc") ||
                (plan.EntryIds?.Any(id => !explicitIds.Contains(id)) ?? false))
                throw new ArgumentException("خطة الذكاء الاصطناعي غير صالحة؛ أعد صياغة الطلب.");
            if (plan.Operation is "latest_created" or "latest_modified" && plan.Limit != 1)
                throw new ArgumentException("طلب آخر وثيقة يجب أن يحدد وثيقة واحدة لكل تقرير.");
        }
        return request;
    }
}
internal sealed record ReportRequest(QueryPlan[] Reports, string? Clarification = null);
