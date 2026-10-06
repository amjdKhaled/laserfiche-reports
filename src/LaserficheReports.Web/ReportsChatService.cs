using System.Net.Http.Json;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;


namespace LaserficheReports.Web;

internal sealed record Evidence(int EntryId, string DocumentName, string Path, int? PageNumber,
    float Similarity, string Text, string TextSource);
internal sealed record ChatResult(string Answer, IReadOnlyList<Evidence> Sources, AnswerScope? Scope = null)
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public ReportQuality? Quality { get; init; }
    public int[] RelatedEntryIds { get; init; } = [];
    public RepositoryQuery? Query { get; init; }
    public LivePagination? Pagination { get; init; }
}
internal sealed record ReportQuality(string Status, bool QuoteVerification, string SemanticReview,
    string PromptVersion, int ModelCalls);
internal sealed record IndexedDocument(int EntryId, string Name, string Path, string Status,
    int ChunkCount, string? TextSource);
internal sealed record IndexedDocumentPage(IReadOnlyList<IndexedDocument> Items, int Page, bool HasMore)
{ public int? TotalCount { get; init; } }

/// <summary>Routes validated requests to live Laserfiche tools only.</summary>
internal sealed class ReportsChatService(IRepositoryContext repositories, LaserficheToolExecutor tools,
    LiveAiClient ai, IConfiguration configuration)
{
    public async Task<IndexedDocumentPage> ListAsync(int page, string? search, CancellationToken ct)
    {
        var query = new RepositoryQuery { Page = page, PageSize = 50, Name = string.IsNullOrWhiteSpace(search) ? null : search.Trim() };
        query.Validate();
        var data = await tools.ExecuteAsync(query, ct);
        return new(data.Items.Select(r => new IndexedDocument(r.EntryId, r.Name, r.FullPath, "live", 0, "laserfiche-metadata-live")).ToArray(),
            page, data.HasMore) { TotalCount = data.TotalCount };
    }

    public async Task<ChatResult> AskAsync(string question, CancellationToken ct,
        RepositoryQuery? previous = null, Func<string, Task>? progress = null)
    {
        if (ReportSupport.NeedsFilterClarification(question)) throw new ArgumentException("هذا الطلب يحتاج أكثر من شرط أو مقارنة غير مدعومة. حدد اسم حقل واحد وقيمة تساوي بدقة.");
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000) throw new ArgumentException("اكتب سؤالًا لا يتجاوز 2000 حرف.");
        var repository = await repositories.GetActiveRepositoryAsync(ct);
        if (progress is not null) await progress("جارٍ فهم السؤال...");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Reports:TimeZone"] ?? "Asia/Riyadh");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        RepositoryQuery? query = null;
        if (previous is not null)
        {
            previous.Validate();
            var page = System.Text.RegularExpressions.Regex.Match(question, @"^اعرض الصفحة (\d+)[.؟?]?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (page.Success && int.TryParse(page.Groups[1].Value, out var pageNumber)) query = previous.ForPage(pageNumber);
        }
        query ??= QuestionRouter.TryRoute(question, today) ?? await ai.PlanAsync(question, previous, await tools.FieldCatalogAsync(ct), ct);
        if (previous is not null && System.Text.RegularExpressions.Regex.IsMatch(question, "النتائج|هذه النتائج|رتبها|لخصها"))
            query = query with { Field = previous.Field, Value = previous.Value, Template = previous.Template,
                Name = previous.Name, EntryId = previous.EntryId, FolderId = previous.FolderId,
                FolderName = previous.FolderName, StartDate = previous.StartDate, EndDate = previous.EndDate, DateField = previous.DateField };
        if (query.Intent == "unsupported") return new("السؤال غير متاح بهذه المرحلة أو يحتاج تحديدًا أوضح. يمكنني البحث في الأسماء والحقول والقوالب والتواريخ والعد وبيانات الوثائق. تحليل محتوى الصور والصفحات غير متاح حاليًا.", []);
        using var queryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queryDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Reports:QueryTimeoutSeconds", 120), 1, 1800)));
        query = await tools.ValidateAsync(query, queryDeadline.Token);
        if (progress is not null) await progress("جارٍ البحث في Laserfiche...");
        var data = await tools.ExecuteAsync(query, queryDeadline.Token);
        if (progress is not null) await progress(data.TotalCount is int total ? $"تم العثور على {total} نتيجة؛ جارٍ إعداد التقرير..." : "جارٍ إعداد النتائج المباشرة...");
        return LaserficheToolExecutor.Format(data, query, repository.RepositoryId);
    }
}
