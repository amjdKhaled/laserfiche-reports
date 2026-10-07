using System.Net.Http.Json;

namespace LaserficheReports.Web;

internal enum QueryKind { LASERFICHE_QUERY, OCR_QUERY, HYBRID_QUERY }
internal sealed record QueryPlan(string Operation, string? Field = null, string? Value = null,
    string? Template = null, int? FolderId = null, string? Name = null, int Limit = 50,
    bool Content = false, string? GroupBy = null, string? From = null, string? To = null, string? Title = null, string? Sort = null, int[]? EntryIds = null, string? Question = null,
    RepositoryFilter? Filters = null, string EntryType = "document", int Page = 1, bool CountOnly = false,
    GroupDimension[]? GroupFields = null, AggregateMetric[]? Metrics = null, string? AggregateSort = null,
    string? SortField = null, string SortDirection = "asc", bool RequireUnique = false, string ContentMode = "summary", AggregateHaving? Having = null, string? Rollup = null, bool AllResults = false, string? ResultType = null, bool RequiresFilter = false)
{
    public void ValidateIntent()
    {
        if (Operation == "clarify")
        {
            if (Filters != null || Field != null || Template != null || FolderId != null || Name != null || EntryIds?.Length > 0 || GroupFields?.Length > 0 || Metrics?.Length > 0 || Having != null || Rollup != null)
                throw new ArgumentException("طلب التوضيح لا يجوز أن ينفذ استعلامًا.");
            return;
        }
        if (ResultType == "documents" && (Operation is not ("search" or "folders" or "recent" or "latest_created" or "latest_modified" or "created" or "modified") || CountOnly || Content) ||
            ResultType == "statistics" && Operation != "group" ||
            ResultType != null && Operation == "group" && ResultType != "statistics" ||
            ResultType == "count" && (!CountOnly || Operation == "group") || ResultType == "content" && !Content)
            throw new ArgumentException("خطة العرض لا تطابق المطلوب؛ لا يمكن استبدال قائمة الوثائق بجدول إحصاءات.");
        if (RequiresFilter && Operation != "clarify" && Filters == null && Field == null && Template == null && FolderId == null && Name == null && !(EntryIds?.Length > 0) && From == null)
            throw new ArgumentException("خطة البحث لا تحتوي الشرط المطلوب؛ لم أنفذ بحثًا غير مقيّد بدلًا منه.");
    }
    public bool CompleteListing => AllResults && Page == 1 && !Content && !CountOnly && !RequireUnique && Operation is "search" or "created" or "modified" or "folders";
    public QueryKind Kind => Content ? Operation == "content" && Filters == null && Field == null && Template == null && FolderId == null && Name == null ? QueryKind.OCR_QUERY : QueryKind.HYBRID_QUERY
        : QueryKind.LASERFICHE_QUERY;
}

internal sealed class QuestionRouter(IHttpClientFactory clients)
{
    public async Task<ReportRequest> RouteAsync(string question, object catalog, CancellationToken cancellationToken,
        ChatTurn[]? history = null)
    {
        var client = clients.CreateClient("ReportsGraph");
        try
        {
            using var health = await client.GetAsync("health", cancellationToken);
            await GraphServiceException.EnsureSuccessAsync(health, "planning", cancellationToken);
            var status = await health.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
            if (!status.TryGetProperty("routingVersion", out var version) || version.GetString() != "schema-agent-v5" ||
                !status.TryGetProperty("modelTimeoutSeconds", out var timeout) || !timeout.TryGetInt32(out var seconds) || (seconds != 0 && seconds < 60))
                throw new GraphServiceException("graph_protocol_mismatch", "planning", GraphServiceException.MessageFor("graph_protocol_mismatch"));
        }
        catch (HttpRequestException error)
        {
            throw new GraphServiceException("graph_unavailable", "planning", GraphServiceException.MessageFor("graph_unavailable"), error);
        }
        HttpResponseMessage routeResponse;
        try { routeResponse = await client.PostAsJsonAsync("route",
            new { question, catalog, today = RepositoryDates.Today().ToString("yyyy-MM-dd"), timezone = "Asia/Riyadh", history = history ?? [] }, cancellationToken); }
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
        var context = question + " " + string.Join(" ", (history ?? []).Select(t => t.Text));
        var mentionedIds = System.Text.RegularExpressions.Regex.Matches(context, @"\p{Nd}+")
            .Select(m => string.Concat(m.Value.Select(c => char.GetNumericValue(c).ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Select(s => int.TryParse(s, out var id) ? id : 0).ToHashSet();
        foreach (var plan in request.Reports)
        {
            if (plan.ResultType is not ("documents" or "count" or "statistics" or "content" or "details" or "schema" or "clarification"))
                throw new ArgumentException("خدمة التخطيط لم تحدد نوع النتيجة المطلوبة؛ أعد تشغيل أحدث خدمة reports-graph.");
            plan.ValidateIntent();
            if (plan.Operation is not ("search" or "folders" or "metadata" or "templates" or "schema" or "folder_information" or "recent" or "latest_created" or "latest_modified" or "created" or "modified" or "group" or "content" or "clarify") ||
                plan.Limit is < 1 or > 200 || plan.Page is < 1 or > 1000000 ||
                plan.Sort is not (null or "creationTime desc" or "creationTime asc" or "lastModifiedTime desc" or "lastModifiedTime asc" or "name asc" or "name desc" or "id asc" or "id desc") ||
                plan.ContentMode is not ("summary" or "search") || plan.SortDirection is not ("asc" or "desc") || plan.EntryType is not ("document" or "folder" or "all") ||
                (plan.EntryIds?.Length > 50) || (plan.EntryIds?.Any(id => id <= 0 || !mentionedIds.Contains(id)) ?? false) ||
                (plan.FolderId is int folder && !mentionedIds.Contains(folder)))
                throw new ArgumentException("خطة الذكاء الاصطناعي غير صالحة؛ أعد صياغة الطلب.");
            if (plan.Operation != "group" && (plan.GroupFields?.Length > 0 || plan.Metrics?.Length > 0 || plan.Having != null || plan.Rollup != null) || plan.Content && plan.CountOnly)
                throw new ArgumentException("خطة الحساب أو المحتوى غير متسقة.");
            if (plan.Operation is "latest_created" or "latest_modified" && plan.Limit != 1)
                throw new ArgumentException("طلب آخر وثيقة يجب أن يحدد وثيقة واحدة لكل تقرير.");
        }
        return request;
    }
}
internal sealed record ReportRequest(QueryPlan[] Reports, string? Clarification = null);
internal sealed record ChatTurn(string Role, string Text);
