using System.Diagnostics;
using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Web;

internal sealed record ToolData(IReadOnlyList<LFSearchResult> Items, int? TotalCount, int Page, int PageSize,
    bool HasMore, bool Exhaustive, string Detail, IReadOnlyDictionary<string, int>? Groups = null);

internal sealed class LaserficheToolExecutor(ILaserficheSearchService search, ILaserficheEntryService entries,
    ILaserficheFieldDefinitionService definitions, ILaserficheTemplateService templates,
    ILaserficheRepositoryService repositories, IConfiguration config, ILogger<LaserficheToolExecutor> logger)
{
    private readonly Dictionary<int,string> folderPaths = new();
    internal async Task<string[]> FieldCatalogAsync(CancellationToken ct) =>
        (await definitions.GetFieldDefinitionsAsync(ct)).Values.Select(f => f.Name).Take(200).ToArray();
    private int RowLimit => Math.Clamp(config.GetValue("Reports:MaxReportRows", 100000), 100, 1000000);

    internal async Task<RepositoryQuery> ValidateAsync(RepositoryQuery query, CancellationToken ct)
    {
        query.Validate();
        if (query.Field is not null || query.GroupBy.Length > 0)
        {
            var fields = await definitions.GetFieldDefinitionsAsync(ct);
            string Resolve(string requested)
            {
                var matches = fields.Values.Where(f => ReportSupport.MatchKey(f.Name) == ReportSupport.MatchKey(requested) ||
                    ReportSupport.MatchKey(requested).EndsWith(ReportSupport.MatchKey(f.Name), StringComparison.Ordinal))
                    .OrderByDescending(f => f.Name.Length).ToArray();
                if (matches.Length == 0) throw new ArgumentException($"لم أجد الحقل «{requested}». اكتب اسمه كما يظهر في Laserfiche.");
                var longest = matches.Where(f => f.Name.Length == matches[0].Name.Length).ToArray();
                if (longest.Length != 1) throw new ArgumentException("اسم الحقل ملتبس. حدد الاسم الكامل.");
                if (query.GroupBy.Contains(requested) && longest[0].IsMultiValue)
                    throw new ArgumentException("تجميع الحقول متعددة القيم غير متاح بهذه الواجهة؛ لا يمكن ضمان توزيع كامل لكل القيم.");
                return longest[0].Name;
            }
            query = query with { Field = query.Field is null ? null : Resolve(query.Field), GroupBy = query.GroupBy.Select(Resolve).ToArray() };
        }
        if (query.Template is not null)
        {
            var found = (await templates.GetTemplateDefinitionsAsync(ct)).Where(t => ReportSupport.MatchKey(t.Name) == ReportSupport.MatchKey(query.Template)).ToArray();
            if (found.Length != 1) throw new ArgumentException("القالب غير موجود أو اسمه ملتبس.");
            query = query with { Template = found[0].Name };
        }
        if (query.Intent != "folder" && query.FolderName is not null)
        {
            var folders = await SearchAsync(new() { EntryType = "folders", Name = query.FolderName, PageSize = 2 }, ct);
            if (folders.Items.Count != 1 || folders.HasNextPage) throw new ArgumentException("اسم المجلد غير موجود أو مكرر. حدد رقمه.");
            query = query with { FolderId = folders.Items[0].EntryId, FolderName = null };
        }
        query.Validate();
        return query;
    }

    internal async Task<PagedResult<LFSearchResult>> SearchAsync(RepositoryQuery query, CancellationToken ct)
    {
        var expression = query.Expression(config["Reports:DateFormat"] ?? "MM/dd/yyyy");
        if (query.FolderId is int id)
        {
            if (id == 0) id = await entries.GetRootEntryIdAsync(ct);
            if (!folderPaths.TryGetValue(id, out var path))
            {
                var folder = await entries.GetEntryAsync(id, ct);
                if (folder.EntryType is not (LFEntryType.Folder or LFEntryType.RecordSeries)) throw new ArgumentException("الإدخال ليس مجلدًا.");
                path = folder.FullPath;
                if (string.IsNullOrEmpty(path) || path.Any(char.IsControl)) throw new InvalidOperationException("لم يرجع Laserfiche مسار مجلد صالحًا.");
                folderPaths.Add(id, path);
            }
            expression += " & {LF:Lookin=\"" + path.Replace("\"", "\\\"") + "\", Subfolders=N}";
        }
        return await search.QueryAsync(expression, query.Page, query.PageSize, query.Sort, query.GroupBy, ct);
    }

    internal async Task<ToolData> ExecuteAsync(RepositoryQuery query, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (query.Intent == "metadata")
            {
                var entry = await entries.GetEntryAsync(query.EntryId!.Value, ct);
                var fields = await entries.GetEntryFieldsAsync(entry.Id, ct);
                var row = FromEntry(entry) with { FieldValues = fields.ToDictionary(f => f.FieldName, f => f.Value) };
                return new([row], 1, 1, 1, false, true, "بيانات الوثيقة الحالية من Laserfiche.");
            }
            if (query.Intent == "templates")
            {
                var all = await templates.GetTemplateDefinitionsAsync(ct);
                return new(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
                    .Select(t => new LFSearchResult { Name = t.Name, TemplateName = t.Name, TemplateId = t.Id }).ToArray(),
                    all.Count, query.Page, query.PageSize, (long)query.Page * query.PageSize < all.Count, true, "تعريفات القوالب الحالية.");
            }
            if (query.Intent == "repository")
            {
                var repository = await repositories.GetRepositoryInfoAsync(ct);
                return new([], null, 1, 1, false, true, System.Text.Json.JsonSerializer.Serialize(repository));
            }
            if (query.Intent == "folder")
            {
                var id = query.FolderId;
                if (id is null)
                {
                    if (string.IsNullOrWhiteSpace(query.FolderName)) throw new ArgumentException("حدد اسم المجلد أو رقمه.");
                    var candidates = await SearchAsync(new() { EntryType = "folders", Name = query.FolderName, PageSize = 2 }, ct);
                    if (candidates.Items.Count != 1 || candidates.HasNextPage) throw new ArgumentException("المجلد غير موجود أو توجد أسماء مكررة؛ حدد رقم المجلد.");
                    id = candidates.Items[0].EntryId;
                }
                if (id == 0) id = await entries.GetRootEntryIdAsync(ct);
                var folder = await entries.GetEntryAsync(id!.Value, ct);
                if (folder.EntryType is not (LFEntryType.Folder or LFEntryType.RecordSeries)) throw new ArgumentException("الإدخال المحدد ليس مجلدًا.");
                var page = await entries.GetEntryChildrenAsync(id.Value, query.Page, query.PageSize, ct);
                return new(page.Items.Select(FromEntry).ToArray(), page.TotalCountIsExact ? page.TotalCount : null,
                    query.Page, query.PageSize, page.HasNextPage, !page.HasNextPage && query.Page == 1, "الأبناء المباشرون للمجلد فقط.");
            }
            if (query.Intent == "count")
            {
                var page = await SearchAsync(query with { Page = 1, PageSize = 1 }, ct);
                if (page.TotalCountIsExact) return new([], page.TotalCount, 1, 1, false, true, "عد دقيق من نتائج بحث Laserfiche ضمن صلاحيات حسابك.");
                var total = 0;
                await foreach (var row in AllAsync(query, ct)) total++;
                return new([], total, 1, 1, false, true, "عد كامل من صفحات نتائج البحث؛ API لم يرجع العدد الإجمالي.");
            }
            if (query.Intent == "report")
            {
                var groups = new Dictionary<string, int>(StringComparer.Ordinal);
                var preview = new List<LFSearchResult>();
                var total = 0;
                await foreach (var row in AllAsync(query, ct))
                {
                    total++;
                    if (preview.Count < query.PageSize) preview.Add(row);
                    if (query.GroupBy.Length > 0)
                    {
                        // Missing projection properties are an API capability failure, never a false empty category.
                        if (query.GroupBy.Any(f => !row.FieldValues.ContainsKey(f)))
                            throw new InvalidOperationException("واجهة Laserfiche لم ترجع الحقول المطلوبة للتجميع. لا يمكن إصدار إحصاء موثوق.");
                        var key = string.Join(" / ", query.GroupBy.Select(f => string.IsNullOrWhiteSpace(row.FieldValues[f]) ? "غير مذكور" : row.FieldValues[f]));
                        groups[key] = groups.GetValueOrDefault(key) + 1;
                    }
                }
                return new(preview, total, 1, query.PageSize, total > preview.Count, true,
                    "التجميع يشمل جميع نتائج البحث. الجدول يعرض صفحة معاينة فقط. النتائج تتبع صلاحيات حسابك؛ ليست لقطة معاملات ثابتة أثناء التغييرات المتزامنة.", groups);
            }
            var result = await SearchAsync(query, ct);
            return new(result.Items, result.TotalCountIsExact ? result.TotalCount : null, query.Page, query.PageSize,
                result.HasNextPage, query.Page == 1 && !result.HasNextPage, "نتائج مباشرة من Laserfiche ضمن صلاحيات حسابك؛ لا تمثل الصفحة وحدها حصرًا لكل المستودع.");
        }
        finally { logger.LogInformation("[PERF] Operation={Operation} DurationMs={DurationMs}", query.Intent, watch.ElapsedMilliseconds); }
    }

    private async IAsyncEnumerable<LFSearchResult> AllAsync(RepositoryQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<int>();
        int? expectedCount = null;
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await SearchAsync(query with { Page = pageNumber, PageSize = 100, Sort = "id asc" }, ct);
            if (page.TotalCountIsExact)
            {
                expectedCount ??= page.TotalCount;
                if (expectedCount != page.TotalCount) throw new InvalidOperationException("تغيّرت نتائج البحث أثناء التقرير. أعد الطلب لضمان اكتماله.");
            }
            foreach (var row in page.Items)
            {
                if (!seen.Add(row.EntryId)) throw new InvalidOperationException("تكررت وثيقة أثناء جمع صفحات التقرير. أعد الطلب.");
                if (seen.Count > RowLimit) throw new InvalidOperationException("تجاوز التقرير حد السجلات المهيأ. ضيّق نطاق البحث؛ لم يتم إصدار إحصاء جزئي.");
                yield return row;
            }
            if (!page.HasNextPage)
            {
                if (expectedCount.HasValue && seen.Count != expectedCount.Value)
                    throw new InvalidOperationException("نتائج التقرير غير مكتملة. أعد الطلب.");
                yield break;
            }
            if (page.Items.Count == 0) throw new InvalidOperationException("واجهة البحث أعادت صفحة فارغة مع نتائج متبقية.");
        }
    }

    internal static LFSearchResult FromEntry(LFEntry entry) => new() { EntryId = entry.Id, Name = entry.Name,
        FullPath = entry.FullPath, EntryType = entry.EntryType, TemplateName = entry.TemplateName,
        TemplateId = entry.TemplateId, CreationTime = entry.CreationTime, LastModifiedTime = entry.LastModifiedTime };

    internal static ChatResult Format(ToolData data, RepositoryQuery query, string repositoryId)
    {
        var report = new StringBuilder("# تقرير Laserfiche\n\n");
        report.AppendLine($"المستودع: {ReportSupport.Cell(repositoryId)}\n");
        if (data.TotalCount is not null) report.AppendLine($"العدد الإجمالي المطابق: **{data.TotalCount}**.\n");
        else report.AppendLine("العدد الإجمالي لم توفره واجهة API؛ لا يتم تقديره.\n");
        report.AppendLine(data.Detail + "\n");
        if (data.Groups?.Count > 0)
        {
            report.AppendLine("| المجموعة | عدد الوثائق | النسبة |\n| --- | --- | --- |");
            foreach (var group in data.Groups.OrderByDescending(g => g.Value).ThenBy(g => g.Key))
                report.AppendLine($"| {ReportSupport.Cell(group.Key)} | {group.Value} | {(data.TotalCount > 0 ? 100m * group.Value / data.TotalCount.Value : 0):0.##}% |");
        }
        var evidence = new List<Evidence>();
        if (data.Items.Count > 0)
        {
            var fields = data.Items.SelectMany(i => i.FieldValues.Keys).Distinct().ToArray();
            report.AppendLine("\n| رقم الإدخال | الاسم | المسار | القالب | الإنشاء | التعديل |" + string.Join("", fields.Select(f => $" {ReportSupport.Cell(f)} |")));
            report.AppendLine("| --- | --- | --- | --- | --- | --- |" + string.Concat(fields.Select(_ => " --- |")));
            foreach (var row in data.Items)
            {
                report.AppendLine($"| {(row.EntryId > 0 ? row.EntryId.ToString() : "—")} | {ReportSupport.Cell(row.Name)} | {ReportSupport.Cell(row.FullPath)} | {ReportSupport.Cell(row.TemplateName)} | {row.CreationTime:yyyy-MM-dd HH:mm zzz} | {row.LastModifiedTime:yyyy-MM-dd HH:mm zzz} |" +
                    string.Concat(fields.Select(f => $" {ReportSupport.Cell(row.FieldValues.GetValueOrDefault(f))} |")));
                if (row.EntryId > 0) evidence.Add(new(row.EntryId, row.Name, row.FullPath, null, 1,
                    string.Join("\n", row.FieldValues.Select(f => $"{f.Key}: {f.Value}")), "laserfiche-metadata-live"));
            }
        }
        else if (data.TotalCount == 0) report.AppendLine("لم يتم العثور على نتائج مطابقة.");
        if (data.HasMore) report.AppendLine($"\nالمعروض صفحة {data.Page} بحجم {data.PageSize}. توجد نتائج إضافية؛ اطلب الصفحة التالية أو استخدم أزرار الصفحات.");
        return new(report.ToString(), evidence, new AnswerScope("repository", repositoryId,
            data.TotalCount ?? data.Items.Count, evidence.Count, data.Exhaustive, data.Detail, query.EntryId is int id ? [id] : []))
            { Query = query, Pagination = new(data.TotalCount, data.Page, data.PageSize, data.HasMore), RelatedEntryIds = evidence.Select(e => e.EntryId).ToArray() };
    }
}
internal sealed record LivePagination(int? TotalCount, int Page, int PageSize, bool HasMore);
