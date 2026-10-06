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
        var client = clients.CreateClient("ReportsGraph");
        try
        {
            using var health = await client.GetAsync("health", cancellationToken);
            await GraphServiceException.EnsureSuccessAsync(health, "planning", cancellationToken);
            var status = await health.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
            if (!status.TryGetProperty("routingVersion", out var version) || version.GetString() != "ai-multi-report-v3" ||
                !status.TryGetProperty("modelTimeoutSeconds", out var timeout) || !timeout.TryGetInt32(out var seconds) || seconds < 60)
                throw new GraphServiceException("graph_protocol_mismatch", "planning", GraphServiceException.MessageFor("graph_protocol_mismatch"));
        }
        catch (HttpRequestException error)
        {
            throw new GraphServiceException("graph_unavailable", "planning", GraphServiceException.MessageFor("graph_unavailable"), error);
        }
        HttpResponseMessage routeResponse;
        try { routeResponse = await client.PostAsJsonAsync("route",
            new { question, catalog, today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd") }, cancellationToken); }
        catch (HttpRequestException error)
        {
            throw new GraphServiceException("graph_unavailable", "planning", GraphServiceException.MessageFor("graph_unavailable"), error);
        }
        using var response = routeResponse;
        await GraphServiceException.EnsureSuccessAsync(response, "planning", cancellationToken);
        var request = await response.Content.ReadFromJsonAsync<ReportRequest>(cancellationToken)
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
