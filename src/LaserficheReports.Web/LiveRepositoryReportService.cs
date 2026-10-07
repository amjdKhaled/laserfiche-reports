using System.Globalization;
using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Web;

/// <summary>Validated, live tools. Search executes on the repository, never by walking its folders.</summary>
internal sealed class LiveRepositoryReportService(ILaserficheEntryService entries,
    ILaserficheSearchService searches, ILaserficheFieldDefinitionService fields,
    ILaserficheTemplateService templates, ILogger<LiveRepositoryReportService> logger)
{
    internal const string Documents = "{LF:Name=\"*\", Type=D}";
    internal static string Term(string value, bool field = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            value.Any(char.IsControl) || value.IndexOfAny(['"', '{', '}', '[', ']', '*', '?']) >= 0 ||
            (field && value.Contains('\\')))
            throw new ArgumentException("اسم الحقل أو القالب أو قيمة البحث غير صالحة. استخدم الاسم والقيمة كما تظهر في Laserfiche.");
        return value.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private IReadOnlyDictionary<int, LFFieldDefinition>? schema;
    private IReadOnlyList<LFTemplateDefinition>? templateSchema;
    private async Task<IReadOnlyList<LFTemplateDefinition>> TemplatesAsync(CancellationToken ct) => templateSchema ??= await templates.GetTemplateDefinitionsAsync(ct);
    private Task<IReadOnlyDictionary<int, LFFieldDefinition>> SchemaAsync(CancellationToken ct) => schema is null ? LoadSchemaAsync(ct) : Task.FromResult(schema);
    private async Task<IReadOnlyDictionary<int, LFFieldDefinition>> LoadSchemaAsync(CancellationToken ct) => schema = await fields.GetFieldDefinitionsAsync(ct);

    private async Task<IReadOnlyList<LFFieldValue>> EntryFieldsAsync(int id, CancellationToken ct)
    {
        var values = await entries.GetEntryFieldsAsync(id, ct);
        var definitions = schema ?? (values.Any(f => string.IsNullOrWhiteSpace(f.FieldName)) ? await SchemaAsync(ct) : null);
        return values.Select(value =>
        {
            var definition = definitions?.GetValueOrDefault(value.FieldDefinitionId);
            if (string.IsNullOrWhiteSpace(value.FieldName) && definition == null)
                throw new ArgumentException("تعذر ربط قيمة الحقل بتعريفه الحالي؛ لم أعرض قيمة ناقصة.");
            return definition == null ? value : value with { FieldName = definition.Name, FieldType = definition.FieldType, IsMultiValue = definition.IsMultiValue };
        }).ToArray();
    }

    private sealed record FieldResolution(string[] Preferred, string[] Equivalent);

    private async Task<FieldResolution> ResolveFieldsAsync(string question, CancellationToken ct)
    {
        var definitions = await SchemaAsync(ct);
        var names = definitions.Values.Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal).ToArray();
        var candidates = names.Where(n => ReportSupport.MatchesField(new FieldCondition(question, ""), n))
            .OrderByDescending(n => ReportSupport.MatchKey(n).Length).ToArray();
        if (candidates.Length == 0) throw new ArgumentException("لم أتعرف على الحقل المطلوب. اكتب اسمه الكامل كما يظهر في Laserfiche.");
        var longest = ReportSupport.MatchKey(candidates[0]);
        candidates = candidates.Where(n => ReportSupport.MatchKey(n) == longest).ToArray();
        var literal = question.Trim().TrimEnd('"', '\'', '«', '»');
        var exact = candidates.FirstOrDefault(n => literal.EndsWith(n, StringComparison.Ordinal));
        // Different IDs with the same name are one search field. If only spelling
        // differs and no literal name was supplied, search each authoritative name.
        return new FieldResolution((exact is null ? candidates : [exact]).Select(n => Term(n, true)).ToArray(),
            candidates.Select(n => Term(n, true)).ToArray());
    }

    public async Task<PagedResult<LFSearchResult>> SelectAsync(QueryPlan plan, IReadOnlyList<int> ids,
        bool readAll, CancellationToken ct)
    {
        plan.ValidateIntent();
        if (plan.Limit is < 1 or > 200 || ids.Count > 50 || ids.Any(id => id <= 0) || plan.FolderId is <= 0)
            throw new ArgumentException("نطاق الطلب غير صالح. حدد حتى 50 وثيقة وحجم نتائج بين 1 و200.");
        var expression = plan.Operation == "folders" || plan.EntryType == "folder" ? "{LF:Name=\"*\", Type=F}" :
            plan.EntryType == "all" ? "{LF:Name=\"*\", Type=DF}" : Documents;
        if (plan.Filters != null) expression += " & " + StructuredRepositoryQuery.Compile(plan.Filters, (await SchemaAsync(ct)).Values, RepositoryDates.Today());
        if (ids.Count > 0) expression += " & (" + string.Join(" | ", ids.Select(id => $"{{LF:ID={id}}}")) + ")";
        string? requestedField = null;
        string? filterClause = null;
        string[] equivalentFields = [];
        if (plan.Field is not null)
        {
            var resolution = await ResolveFieldsAsync(plan.Field, ct);
            var resolvedFields = resolution.Preferred;
            equivalentFields = resolution.Equivalent;
            requestedField = resolvedFields.Length == 1 ? resolvedFields[0] : null;
            if (plan.Value is null) throw new ArgumentException("اكتب قيمة الحقل المطلوب.");
            var value = Term(plan.Value);
            var filters = resolvedFields.Select(name => $"{{[]:[{name}]=\"{value}\"}}").ToArray();
            filterClause = filters.Length == 1 ? filters[0] : "(" + string.Join(" | ", filters) + ")";
            expression += " & " + filterClause;
        }
        if (plan.Template is not null)
        {
            var definitions = await TemplatesAsync(ct);
            var template = definitions.SingleOrDefault(t => ReportSupport.MatchKey(t.Name) == ReportSupport.MatchKey(plan.Template))
                ?? throw new ArgumentException("القالب المطلوب غير موجود في المستودع الحالي.");
            expression += $" & {{[{Term(template.Name, true)}]:[]}}";
        }
        if (plan.Name is not null) expression += $" & {{LF:Name=\"{Term(plan.Name)}\", Type={(plan.EntryType == "folder" || plan.Operation == "folders" ? "F" : plan.EntryType == "all" ? "DF" : "D")}}}";
        if (plan.FolderId is int folderId)
        {
            var folder = await entries.GetEntryAsync(folderId, ct);
            if (folder.EntryType is not (LFEntryType.Folder or LFEntryType.RecordSeries))
                throw new ArgumentException("الإدخال المحدد ليس مجلدًا.");
            var path = await entries.GetEntryPathAsync(folderId, ct);
            expression += $" & {{LF:Lookin=\"{Term(path)}\", Subfolders=N}}";
        }
        if (plan.Operation is "created" or "modified")
        {
            string Date(string? text)
            {
                if (text == "today") return "now";
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new ArgumentException("اكتب التاريخ بصيغة yyyy-MM-dd.");
                // ISO avoids workstation-dependent day/month order; verify server locale in acceptance testing.
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            var start = Date(plan.From); var end = Date(plan.To);
            if (start != "now" && end != "now" && string.CompareOrdinal(start, end) > 0)
                throw new ArgumentException("تاريخ البداية يجب أن يسبق تاريخ النهاية.");
            var key = plan.Operation == "created" ? "Created" : "Modified";
            expression += start == end ? $" & {{LF:{key}=\"{start}\"}}" :
                $" & {{LF:{key}>=\"{start}\"}} & {{LF:{key}<=\"{end}\"}}";
        }
        if (plan.GroupBy is not null && plan.GroupBy != "template")
        {
            var resolvedFields = (await ResolveFieldsAsync(plan.GroupBy, ct)).Preferred;
            if (resolvedFields.Length != 1)
                throw new ArgumentException("حدد حقل التجميع من الأسماء الموجودة: " + string.Join("، ", resolvedFields));
            requestedField = resolvedFields[0];
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (plan.GroupFields?.Length > 0 || plan.Metrics?.Length > 0 || plan.SortField != null || plan.Having != null || plan.Rollup != null)
            RepositoryAggregation.Validate(plan, (await SchemaAsync(ct)).Values);
        var projection = (plan.GroupFields ?? []).Select(g => g.Field).Concat((plan.Metrics ?? []).Select(m => m.Field).OfType<string>())
            .Concat(plan.SortField is null ? [] : new[] { plan.SortField }).Where(f => !StructuredRepositoryQuery.Builtins.Contains(f))
            .Select(f => StructuredRepositoryQuery.ResolveField(f, (schema ?? throw new InvalidOperationException("Schema not loaded.")).Values).Name).Distinct().ToArray();
        var result = await searches.QueryAsync(expression, readAll ? 1 : plan.Page, plan.CountOnly ? 1 : plan.Limit,
            Sort(plan), requestedField, readAll, ct, projection);
        if (filterClause is not null && result.IsTotalCountExact && result.TotalCount == 0)
            result = await ReadNormalizedMatchesAsync(expression, filterClause, equivalentFields, plan, readAll,
                plan.GroupBy is not null && plan.GroupBy != "template" ? requestedField : null, ct);
        logger.LogInformation("Stage=LASERFICHE Tool={Tool} DurationMs={DurationMs} TotalCount={TotalCount} Exact={Exact}",
            plan.Operation, watch.ElapsedMilliseconds, result.TotalCount, result.IsTotalCountExact);
        return result;
    }

    private async Task<PagedResult<LFSearchResult>> ReadNormalizedMatchesAsync(string expression, string filterClause,
        string[] fieldNames, QueryPlan plan, bool readAll, string? groupField, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(300));
        ct = budget.Token;
        var matches = new Dictionary<int, LFSearchResult>();
        foreach (var name in fieldNames)
        {
            // Search only populated instances of the requested field, preserving
            // every ID, template, folder, name and date restriction of the plan.
            var presence = expression.Replace(" & " + filterClause, $" & {{[]:[{name}]=\"*\"}}", StringComparison.Ordinal);
            var candidates = await searches.QueryAsync(presence, 1, 200,
                Sort(plan), name, true, ct);
            if (candidates.HasNextPage || !candidates.IsTotalCountExact)
                throw new ArgumentException("تعذر التحقق من جميع قيم الحقل. حدد نطاقًا أضيق وأعد المحاولة.");
            foreach (var item in candidates.Items)
            {
                var projected = item.Fields.Where(f => string.Equals(f.Name, name, StringComparison.Ordinal)).ToArray();
                if (projected.Length == 0)
                    throw new ArgumentException("لم يرجع Laserfiche قيم الحقل المطلوبة؛ تعذر تأكيد نتائج البحث.");
                var match = projected.Any(f => f.Values.Any(v => ReportSupport.MatchesValue(v, plan.Value!)));
                IReadOnlyList<LFFieldValue>? full = null;
                if (projected.Any(f => f.HasMoreValues))
                {
                    full = await EntryFieldsAsync(item.EntryId, ct);
                    match = full.Any(f => string.Equals(f.FieldName, name, StringComparison.Ordinal) &&
                        ReportSupport.MatchesValue(f.Value, plan.Value!, f.IsMultiValue));
                }
                if (!match) continue;
                var reportItem = item;
                if (groupField is not null)
                {
                    full ??= await EntryFieldsAsync(item.EntryId, ct);
                    var groupValues = full.Where(f => string.Equals(f.FieldName, groupField, StringComparison.Ordinal))
                        .Select(f => new LFSearchField(f.FieldName, [f.Value ?? ""], false)).ToArray();
                    if (groupValues.Length == 0)
                        throw new ArgumentException("تعذر الحصول على قيم حقل التجميع من Laserfiche.");
                    reportItem = item with { Fields = groupValues };
                }
                matches[item.EntryId] = reportItem;
            }
        }
        logger.LogInformation("Stage=FIELD_VALUE_CHECK FieldCount={FieldCount} MatchedCount={MatchedCount}",
            fieldNames.Length, matches.Count);
        return new PagedResult<LFSearchResult>
        {
            Items = (readAll ? matches.Values.AsEnumerable() : matches.Values.Take(plan.Limit)).ToArray(),
            TotalCount = matches.Count, IsTotalCountExact = true, PageNumber = 1, PageSize = plan.Limit,
            HasMore = !readAll && matches.Count > plan.Limit
        };
    }

    private async Task<PagedResult<LFSearchResult>> CompleteProjectionAsync(PagedResult<LFSearchResult> result, QueryPlan plan, CancellationToken ct)
    {
        var names = (plan.GroupFields ?? []).Select(g => g.Field).Concat((plan.Metrics ?? []).Select(m => m.Field).OfType<string>())
            .Concat(plan.SortField is null ? [] : new[] { plan.SortField }).Where(f => !StructuredRepositoryQuery.Builtins.Contains(f)).Distinct().ToArray();
        var items = result.Items.ToArray();
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
        {
            var item = items[index];
            if (item.PageCount == null && ((plan.Metrics ?? []).Any(m => m.Field == "pageCount") || plan.SortField == "pageCount" || (plan.GroupFields ?? []).Any(g => g.Field == "pageCount")))
            {
                item = item with { PageCount = (await entries.GetEntryAsync(item.EntryId, token)).PageCount };
                if (item.PageCount == null) throw new ArgumentException("عدد الصفحات غير متاح لبعض الإدخالات؛ تعذر حسابه بدقة.");
                items[index] = item;
            }
            if (names.Any(n => !item.Fields.Any(f => f.Name == n && !f.HasMoreValues)))
            {
                var complete = await EntryFieldsAsync(item.EntryId, token);
                items[index] = item with { Fields = names.Select(n =>
                {
                    var f = complete.FirstOrDefault(f => f.FieldName == n);
                    return new LFSearchField(n, f?.Value is null ? [] : f.IsMultiValue ? f.Value.Split(", ") : [f.Value], false);
                }).ToArray() };
            }
        });
        return result with { Items = items };
    }

    private static string Sort(QueryPlan plan) => plan.Operation switch
    {
        "latest_created" => "creationTime desc",
        "latest_modified" or "recent" => "lastModifiedTime desc",
        _ => plan.Sort ?? "id asc"
    };
    private static string Date(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "غير متاح";
    internal async Task<object> CatalogAsync(CancellationToken ct)
    {
        var schemaTask = SchemaAsync(ct);
        var templatesTask = TemplatesAsync(ct);
        await Task.WhenAll(schemaTask, templatesTask);
        var definitions = await schemaTask;
        var templateDefinitions = await templatesTask;
        return new { tool = "GetRepositorySchema", fields = definitions.Values.Select(f => new { f.Name, f.FieldType, f.IsMultiValue, f.IsRequired }).Distinct().ToArray(),
            templates = templateDefinitions.Select(t => t.Name).ToArray(), entryProperties = StructuredRepositoryQuery.Builtins,
            tools = new[] { "SearchEntries", "AggregateEntries", "GetEntry", "GetEntryMetadata", "GetFolderContents", "GetTemplates", "GetRepositorySchema", "GetFolderInformation", "GetOcrContent" } };
    }

    public async Task<ChatResult> CreateAsync(string repositoryId, QueryPlan plan, IReadOnlyList<int> ids,
        CancellationToken ct)
    {
        if (plan.Operation == "schema")
        {
            if (plan.Template != null) throw new ArgumentException("هذه الأداة تعرض تعريفات حقول المستودع؛ ربط الحقول بتعريف قالب بعينه غير متاح من عقد الخدمة الحالي.");
            var definitions = (await SchemaAsync(ct)).Values;
            if (plan.Field != null) definitions = [StructuredRepositoryQuery.ResolveField(plan.Field, definitions)];
            return new ChatResult("# حقول المستودع الحالية\n\n| الحقل | النوع | متعدد القيم | إلزامي |\n| --- | --- | --- | --- |\n" + string.Join("\n", definitions.Select(f => $"| {ReportSupport.Cell(f.Name)} | {ReportSupport.Cell(f.FieldType)} | {f.IsMultiValue} | {f.IsRequired} |")), []);
        }
        if (plan.Operation == "folder_information")
        {
            if (plan.FolderId is not int folderId) throw new ArgumentException("حدد رقم المجلد.");
            var folder = await entries.GetEntryAsync(folderId, ct);
            if (folder.EntryType is not (LFEntryType.Folder or LFEntryType.RecordSeries)) throw new ArgumentException("الإدخال ليس مجلدًا.");
            return new ChatResult($"المجلد {folderId}: {ReportSupport.Cell(folder.Name)}\nالمسار: {ReportSupport.Cell(string.IsNullOrWhiteSpace(folder.FullPath) ? await entries.GetEntryPathAsync(folderId, ct) : folder.FullPath)}", []) { RelatedEntryIds = [folderId] };
        }
        if (plan.Operation == "templates")
        {
            var list = await TemplatesAsync(ct);
            return new ChatResult("# قوالب المستودع\n\n| رقم القالب | اسم القالب |\n| --- | --- |\n" +
                string.Join("\n", list.Select(t => $"| {t.Id} | {ReportSupport.Cell(t.Name)} |")), []);
        }
        if (plan.Operation == "metadata")
        {
            if (ids.Count == 0) throw new ArgumentException("حدد رقم الوثيقة المطلوبة.");
            if (ids.Count > 50 || ids.Any(id => id <= 0)) throw new ArgumentException("حدد حتى 50 إدخالًا صالحًا.");
            var sources = new Evidence[ids.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, ids.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
            {
                var id = ids[index];
                var entry = await entries.GetEntryAsync(id, token);
                var values = await EntryFieldsAsync(id, token);
                var path = string.IsNullOrWhiteSpace(entry.FullPath) ? await entries.GetEntryPathAsync(id, token) : entry.FullPath;
                sources[index] = new Evidence(id, entry.Name, path, null, 1,
                    $"الاسم: {entry.Name}\nالمسار: {path}\nالقالب: {entry.TemplateName}\nتاريخ الإنشاء: {entry.CreationTime}\nتاريخ التعديل: {entry.LastModifiedTime}\n" +
                    string.Join("\n", values.Select(f => $"{f.FieldName}: {f.Value}")), "laserfiche-metadata-live");
            });
            return new ChatResult(string.Join("\n\n", sources.Select(e => $"## الوثيقة {e.EntryId}\n\n" + e.Text)), sources)
                { RelatedEntryIds = ids.ToArray() };
        }
        var result = await SelectAsync(plan, ids, plan.CompleteListing || plan.Operation == "group" || plan.SortField != null, ct);
        if (plan.CompleteListing && (result.HasNextPage || !result.IsTotalCountExact || result.Items.Count != result.TotalCount))
            throw new ArgumentException("تعذر تحميل جميع الوثائق المطابقة؛ لم أعرض قائمة ناقصة على أنها كاملة.");
        if (plan.RequireUnique && (!result.IsTotalCountExact || result.TotalCount != 1))
            return new ChatResult("حدد رقم الوثيقة؛ الاسم يطابق أكثر من إدخال أو لم يمكن تحديد وثيقة واحدة.\n\n" + string.Join("\n", result.Items.Select(i => $"- {i.EntryId}: {ReportSupport.Cell(i.Name)}")), []);
        if (plan.CountOnly && plan.Operation != "group")
            return new ChatResult(result.IsTotalCountExact ? $"عدد النتائج المطابقة: **{result.TotalCount}**." : "العدد الإجمالي غير متاح؛ لم أقدّر العدد من الصفحة المعروضة.", [],
                new AnswerScope("repository", repositoryId, result.TotalCount, 0, result.IsTotalCountExact, "عدد حي من Laserfiche.", ids));
        if (plan.GroupFields?.Length > 0 || plan.Metrics?.Length > 0 || plan.SortField != null || plan.Having != null || plan.Rollup != null)
            result = await CompleteProjectionAsync(result, plan, ct);
        if (plan.Operation == "group" && (plan.GroupFields?.Length > 0 || plan.Metrics?.Length > 0 || plan.GroupBy == null))
            return RepositoryAggregation.Render(repositoryId, plan, result, (await SchemaAsync(ct)).Values, ids);
        if (plan.SortField != null) result = RepositoryAggregation.SortPage(result, plan, (await SchemaAsync(ct)).Values);
        if (plan.Operation == "group")
        {
            if (result.HasNextPage) throw new InvalidOperationException("An aggregation requires all live search results.");
            if (plan.GroupBy != "template" && result.Items.Any(i => i.Fields.Count == 0 || i.Fields.Any(f => f.HasMoreValues)))
                throw new ArgumentException("حقل التجميع متعدد القيم ويحتاج نطاقًا أدق؛ لم أعرض أعدادًا ناقصة.");
            var groups = result.Items.GroupBy(i => plan.GroupBy == "template" ? i.TemplateName ?? "بدون قالب" :
                string.Join(", ", i.Fields.SelectMany(f => f.Values)) is { Length: > 0 } v ? v : "غير مذكور")
                .Select(g => new { Name = g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).ToArray();
            var table = "# توزيع الوثائق\n\n| المجموعة | عدد الوثائق | النسبة |\n| --- | --- | --- |\n" +
                string.Join("\n", groups.Select(g => $"| {ReportSupport.Cell(g.Name)} | {g.Count} | {(result.Items.Count == 0 ? 0 : 100m * g.Count / result.Items.Count):F2}% |"));
            return new ChatResult(table, [], new AnswerScope("repository", repositoryId, result.Items.Count, 0, true,
                "حُسب التوزيع من نتائج البحث الحالية المتاحة لحسابك.", ids));
        }
        // Reuse search values. Only missing page counts require details, bounded to 4 calls.
        var enriched = new LFSearchResult[result.Items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, result.Items.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
        {
            var item = result.Items[index];
            if (item.PageCount is null && item.EntryType is not (LFEntryType.Folder or LFEntryType.RecordSeries))
            {
                try
                {
                    var detail = await entries.GetEntryAsync(item.EntryId, token);
                    item = item with { PageCount = detail.PageCount, FullPath = string.IsNullOrWhiteSpace(item.FullPath) ? detail.FullPath : item.FullPath };
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is LaserficheReports.Domain.Exceptions.LaserficheException or HttpRequestException or OperationCanceledException)
                { logger.LogWarning("Page count unavailable EntryId={EntryId} ErrorType={ErrorType}", item.EntryId, error.GetType().Name); }
            }
            enriched[index] = item;
        });
        result = result with { Items = enriched };
        var evidence = result.Items.Select(i => new Evidence(i.EntryId, i.Name, i.FullPath, null, 1,
            $"الاسم: {i.Name}\nالمسار: {i.FullPath}", "laserfiche-metadata-live")).ToArray();
        var latest = plan.Operation is "latest_created" or "latest_modified";
        var detail = latest ? $"عُرضت **{result.Items.Count}** وثيقة حسب أحدث تاريخ { (plan.Operation == "latest_created" ? "إنشاء" : "تعديل") }." : result.IsTotalCountExact ? $"عدد النتائج المطابقة: **{result.TotalCount}**." :
            "تعذر تأكيد إجمالي النتائج من استجابة المستودع؛ العدد الإجمالي غير متاح.";
        if (!latest && result.HasNextPage) detail += $" عُرضت {result.Items.Count} نتيجة فقط؛ هذه ليست القائمة الكاملة.";
        var tableRows = string.Join("\n", result.Items.Select((i, index) =>
            $"| {i.EntryId} | {ReportSupport.Cell(i.Name)} | {Date(i.CreationTime)} | {Date(i.LastModifiedTime)} | {i.PageCount?.ToString(CultureInfo.InvariantCulture) ?? "—"} | [{index + 1}] |"));
        return new ChatResult("# " + ReportSupport.Cell(plan.Title ?? "تقرير المستودع") + "\n\n" + detail + "\n\n| رقم الوثيقة | اسم الوثيقة | تاريخ الإنشاء | آخر تعديل | عدد الصفحات | المرجع |\n| --- | --- | --- | --- | --- | --- |\n" +
            (result.Items.Count == 0 ? "| — | لم يتم العثور على نتائج مطابقة | — | — | — | — |" : tableRows), evidence,
            new AnswerScope(ids.Count > 0 ? "selected-documents" : "repository", repositoryId,
                result.IsTotalCountExact ? result.TotalCount : result.Items.Count, evidence.Length,
                result.IsTotalCountExact && !result.HasNextPage, detail, ids))
            { RelatedEntryIds = evidence.Select(e => e.EntryId).ToArray() };
    }
}
