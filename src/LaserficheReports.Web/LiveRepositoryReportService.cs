using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;

namespace LaserficheReports.Web;

/// <summary>Live metadata reports never rely on top-k chunks or model-generated counts.</summary>
internal sealed class LiveRepositoryReportService(ILaserficheEntryService entries, IConfiguration configuration)
{
    public async Task<ChatResult> CreateAsync(string repositoryId, FieldCondition? condition,
        IReadOnlyList<int> requestedIds, CancellationToken cancellationToken)
    {
        var documents = new Dictionary<int, LFEntry>();
        var skipped = 0;
        var truncated = false;
        var maxDocuments = Math.Clamp(configuration.GetValue<int?>("Reports:MaxLiveDocuments") ?? 10000, 1, 1000000);
        if (requestedIds.Count > 0)
        {
            foreach (var id in requestedIds)
            {
                try
                {
                    var entry = await entries.GetEntryAsync(id, cancellationToken);
                    if (entry.EntryType == LFEntryType.Document) documents.TryAdd(id, entry);
                    else skipped++;
                }
                catch (LaserficheException ex) when (ex.StatusCode is 403 or 404) { skipped++; }
            }
        }
        else
        {
            var folders = new Queue<int>();
            var seenFolders = new HashSet<int>();
            folders.Enqueue(await entries.GetRootEntryIdAsync(cancellationToken));
            while (folders.Count > 0 && !truncated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderId = folders.Dequeue();
                if (!seenFolders.Add(folderId)) continue;
                IReadOnlyList<LFEntry> children;
                try { children = await entries.GetAllFolderChildrenAsync(folderId, cancellationToken); }
                catch (LaserficheException ex) when (ex.StatusCode is 403 or 404) { skipped++; continue; }
                foreach (var entry in children)
                {
                    if (entry.EntryType is LFEntryType.Folder or LFEntryType.RecordSeries) folders.Enqueue(entry.Id);
                    else if (entry.EntryType == LFEntryType.Document && !documents.ContainsKey(entry.Id))
                    {
                        if (documents.Count >= maxDocuments) { truncated = true; break; }
                        documents.Add(entry.Id, entry);
                    }
                }
            }
        }

        var matches = new List<(LFEntry Entry, IReadOnlyList<LFFieldValue> Fields)>();
        var knownField = false;
        var inspected = 0;
        foreach (var candidate in documents.Values.OrderBy(x => x.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Recheck access even if the folder enumeration returned a cached row.
                var entry = await entries.GetEntryAsync(candidate.Id, cancellationToken);
                if (entry.EntryType != LFEntryType.Document) { skipped++; continue; }
                if (condition is null)
                {
                    inspected++;
                    var metadata = requestedIds.Count > 0
                        ? await entries.GetEntryFieldsAsync(entry.Id, cancellationToken)
                        : Array.Empty<LFFieldValue>();
                    matches.Add((entry, metadata));
                    continue;
                }
                var fields = await entries.GetEntryFieldsAsync(entry.Id, cancellationToken);
                inspected++;
                var relevant = fields.Where(field => ReportSupport.MatchesField(condition, field.FieldName))
                    .OrderByDescending(field => ReportSupport.MatchKey(field.FieldName).Length).ToArray();
                // Prefer the full field name over a shorter suffix such as «الوثيقة».
                if (relevant.Length > 0)
                    relevant = relevant.Where(field => ReportSupport.MatchKey(field.FieldName).Length ==
                        ReportSupport.MatchKey(relevant[0].FieldName).Length).ToArray();
                knownField |= relevant.Length > 0;
                // Multiple matching field definitions with different names are ambiguous.
                if (relevant.Select(x => ReportSupport.MatchKey(x.FieldName)).Distinct().Count() > 1)
                    throw new ArgumentException("اسم الحقل غير محدد. اكتب اسم الحقل الكامل كما يظهر في Laserfiche.");
                var match = relevant.FirstOrDefault(field =>
                    ReportSupport.MatchesValue(field.Value, condition.ExpectedValue, field.IsMultiValue));
                if (match is not null) matches.Add((entry, [match]));
            }
            catch (LaserficheException ex) when (ex.StatusCode is 403 or 404) { skipped++; }
            // Outages and authentication failures must abort rather than produce a false full report.
        }

        var complete = !truncated && skipped == 0;
        var target = requestedIds.Count > 0 ? "الوثائق المحددة" : "المستودع المتاح لحسابك";
        var detail = complete ? $"فُحصت {inspected} وثيقة من {target} باستخدام بيانات Laserfiche الحالية."
            : $"فُحصت {inspected} وثيقة؛ التقرير جزئي" +
              (truncated ? " بسبب بلوغ حد الفحص المهيأ." : " بسبب وثائق أو مجلدات تعذر الوصول إليها.");
        var includeFields = condition is not null || requestedIds.Count > 0;
        var scope = new AnswerScope(requestedIds.Count > 0 ? "selected-documents" : "repository",
            repositoryId, inspected, condition is null ? matches.Sum(x => x.Fields.Count) : matches.Count,
            complete, detail, requestedIds);
        var report = new StringBuilder(condition is not null ? "# تقرير مطابقة حقول الوثائق\n\n" :
            includeFields ? "# تقرير بيانات الوثائق\n\n" : "# تقرير وثائق المستودع\n\n");
        report.AppendLine($"تاريخ إعداد التقرير: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC\n");
        report.AppendLine("## ملخص التقرير\n");
        if (condition is not null && !knownField)
            report.AppendLine("لم أتعرف على اسم الحقل المطلوب ضمن الوثائق المفحوصة. اكتب اسم الحقل الكامل متبوعًا بـ «يساوي» ثم القيمة.");
        else
            report.AppendLine(condition is null ? $"عدد الوثائق المدرجة: **{matches.Count}**؛ عدد قيم الحقول المسترجعة: **{matches.Sum(x => x.Fields.Count)}**."
                : $"عدد الوثائق المطابقة: **{matches.Count}**. القيمة المطلوبة: **{ReportSupport.Cell(condition.ExpectedValue)}**.");
        report.AppendLine($"\n{detail}\n");
        report.AppendLine("## النتائج\n");
        report.AppendLine(condition is not null || includeFields
            ? "| رقم الوثيقة | اسم الوثيقة | الحقل | القيمة | المسار | المرجع |\n| --- | --- | --- | --- | --- | --- |"
            : "| رقم الوثيقة | اسم الوثيقة | المسار | المرجع |\n| --- | --- | --- | --- |");
        var evidence = new List<Evidence>();
        for (var i = 0; i < matches.Count; i++)
        {
            var (entry, fields) = matches[i];
            var path = string.IsNullOrWhiteSpace(entry.FullPath) ? documents[entry.Id].FullPath : entry.FullPath;
            if (includeFields)
            {
                if (fields.Count == 0)
                {
                    report.AppendLine($"| {entry.Id} | {ReportSupport.Cell(entry.Name)} | لا توجد حقول متاحة | غير مذكور | {ReportSupport.Cell(path)} | [{evidence.Count + 1}] |");
                    evidence.Add(new Evidence(entry.Id, entry.Name, path, null, 1,
                        $"اسم الوثيقة: {entry.Name}\nلا توجد حقول متاحة\nالمسار: {path}", "laserfiche-metadata-live"));
                }
                foreach (var field in fields)
                {
                    report.AppendLine($"| {entry.Id} | {ReportSupport.Cell(entry.Name)} | {ReportSupport.Cell(field.FieldName)} | {ReportSupport.Cell(field.Value)} | {ReportSupport.Cell(path)} | [{evidence.Count + 1}] |");
                    evidence.Add(new Evidence(entry.Id, entry.Name, path, null, 1,
                        $"اسم الوثيقة: {entry.Name}\nالمسار: {path}\n{field.FieldName}: {field.Value}", "laserfiche-metadata-live"));
                }
            }
            else
            {
                report.AppendLine($"| {entry.Id} | {ReportSupport.Cell(entry.Name)} | {ReportSupport.Cell(path)} | [{i + 1}] |");
                evidence.Add(new Evidence(entry.Id, entry.Name, path, null, 1,
                    $"اسم الوثيقة: {entry.Name}\nالمسار: {path}\nنوع الإدخال: وثيقة", "laserfiche-metadata-live"));
            }
        }
        if (matches.Count == 0) report.AppendLine(condition is not null || includeFields
            ? "| — | لا توجد نتائج مؤكدة | — | — | — | — |"
            : "| — | لا توجد نتائج مؤكدة | — | — |");
        report.AppendLine("\n## ملاحظات\n\nالنتائج مبنية على حقول المستودع وقت الفحص؛ لا تعتمد على OCR أو التخمين اللغوي.");
        if (includeFields) report.AppendLine("القيمة «غير مذكور» تعني أن Laserfiche لم يُرجع قيمة لهذا الحقل في وقت الفحص.");
        if (!complete) report.AppendLine("الفحص غير مكتمل؛ الأعداد المذكورة تخص الوثائق المفحوصة فقط.");
        return new ChatResult(report.ToString().Trim(), evidence, scope)
            { RelatedEntryIds = evidence.Select(e => e.EntryId).Distinct().ToArray() };
    }
}
