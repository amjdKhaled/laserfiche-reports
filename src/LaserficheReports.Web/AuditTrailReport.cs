using System.Globalization;

namespace LaserficheReports.Web;

internal sealed record AuditQuery(string[]? EventTypes = null, string? UserName = null,
    string? From = null, string? To = null, RelativeDate? FromRelative = null,
    RelativeDate? ToRelative = null, string? GroupBy = null, string? Outcome = null);

internal static class AuditTrailReport
{
    internal static void Validate(QueryPlan plan)
    {
        if (plan.Audit is null || plan.Filters != null || plan.Field != null || plan.Value != null || plan.Template != null ||
            plan.FolderId != null || plan.FolderName != null || plan.Name != null || plan.Content ||
            plan.From != null || plan.To != null || plan.GroupBy != null || plan.GroupFields?.Length > 0 ||
            plan.Metrics?.Length > 0 || plan.Having != null || plan.Rollup != null || plan.SortField != null || plan.Sort != null ||
            plan.AggregateSort != null || plan.RequireUnique || plan.IncludeSubfolders || plan.EntryType != "document" ||
            plan.Limit is < 1 or > 200 || plan.Page is < 1 or > 1000000 || plan.EntryIds?.Length > 50 ||
            (plan.EntryIds?.Any(id => id <= 0) ?? false))
            throw new ArgumentException("خطة التدقيق تحتوي خيارات غير مدعومة؛ استخدم شروط أحداث التدقيق فقط.");
        var query = plan.Audit;
        if (plan.RequiresFilter && query.EventTypes is not { Length: > 0 } && query.UserName == null && query.Outcome == null && query.From == null && query.To == null && query.FromRelative == null && query.ToRelative == null && plan.EntryIds is not { Length: > 0 })
            throw new ArgumentException("شرط تقرير التدقيق المطلوب غير محدد؛ لم أنفذ تقريرًا غير مقيد بدلًا منه.");
        if (query.EventTypes?.Length > 200 || query.EventTypes?.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 200) == true ||
            query.UserName?.Length > 200 || query.Outcome?.Length > 200 ||
            query.GroupBy is not (null or "userName" or "eventType" or "outcome" or "entryId") ||
            query.From != null && query.FromRelative != null || query.To != null && query.ToRelative != null ||
            plan.CountOnly && query.GroupBy != null ||
            plan.ResultType != null && plan.ResultType != (plan.CountOnly ? "count" : query.GroupBy != null ? "statistics" : "details"))
            throw new ArgumentException("شروط أو نوع نتيجة تقرير التدقيق غير صالحة.");
    }

    internal static async Task<ChatResult> CreateAsync(AuditTrailSource source, string repositoryId, QueryPlan plan, CancellationToken ct)
    {
        Validate(plan);
        var data = await source.ReadAsync(ct);
        var query = plan.Audit!;
        DateTimeOffset? Bound(string? literal, RelativeDate? relative)
        {
            if (literal == null && relative == null) return null;
            var date = relative != null ? RepositoryDates.Resolve(relative, RepositoryDates.Today()) :
                DateOnly.TryParseExact(literal, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed : throw new ArgumentException("تاريخ التدقيق يجب أن يكون yyyy-MM-dd.");
            // User dates are Riyadh calendar dates; export timestamps are requested in UTC.
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone));
        }
        var from = Bound(query.From, query.FromRelative); var to = Bound(query.To, query.ToRelative);
        if (from != null && to != null && from >= to) throw new ArgumentException("نطاق التدقيق يجب أن يبدأ قبل نهايته الحصرية.");
        var fields = data.Fields.ToHashSet(StringComparer.Ordinal);
        if (plan.EntryIds?.Length > 0 && !fields.Contains("entryId") || query.Outcome != null && !fields.Contains("outcome") ||
            query.GroupBy != null && !fields.Contains(query.GroupBy))
            throw new ArgumentException("عمود الشرط المطلوب غير متاح في تقرير التدقيق المحفوظ.");
        var known = data.Events.Select(e => e.EventType).ToHashSet(StringComparer.Ordinal);
        if (query.EventTypes?.Any(e => !known.Contains(e) && !data.EventLabels.ContainsKey(e)) == true)
            throw new ArgumentException("نوع الحدث غير معروف في مصدر التدقيق؛ لا يمكن تخمين رمز الحذف أو العرض.");
        var selected = data.Events.Where(e =>
            (query.EventTypes is not { Length: > 0 } || query.EventTypes.Contains(e.EventType, StringComparer.Ordinal)) &&
            (query.UserName == null || string.Equals(query.UserName, e.UserName, StringComparison.OrdinalIgnoreCase)) &&
            (query.Outcome == null || string.Equals(query.Outcome, e.Outcome, StringComparison.OrdinalIgnoreCase)) &&
            (plan.EntryIds is not { Length: > 0 } || e.EntryId != null && plan.EntryIds.Contains(e.EntryId.Value)) &&
            (from == null || e.EventTime >= from) && (to == null || e.EventTime < to)).ToArray();
        string Label(string value) => data.EventLabels.GetValueOrDefault(value, value);
        string Cell(string value) => ReportSupport.Cell(value);
        string table;
        if (plan.CountOnly)
            table = "| المقياس | العدد داخل التقرير المحفوظ |\n| --- | --- |\n| أحداث مطابقة للشروط (قد تتكرر الوثيقة) | " + selected.Length + " |\n";
        else if (query.GroupBy != null)
        {
            string Key(AuditEvent e) => query.GroupBy switch {
                "userName" => e.UserName, "eventType" => e.EventType, "outcome" => e.Outcome,
                _ => e.EntryId?.ToString(CultureInfo.InvariantCulture) ?? "غير مرتبط بوثيقة" };
            var groups = selected.GroupBy(Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
            table = "| " + query.GroupBy + " | عدد الأحداث |\n| --- | --- |\n" +
                string.Join("\n", groups.Skip((plan.Page - 1) * plan.Limit).Take(plan.Limit).Select(g => $"| {Cell(query.GroupBy == "eventType" ? Label(g.Key) + " (" + g.Key + ")" : g.Key)} | {g.Count()} |")) +
                $"\n\n| إجمالي المجموعات | الصفحة |\n| --- | --- |\n| {groups.Length} | {plan.Page} |\n";
        }
        else
        {
            var rows = selected.OrderByDescending(e => e.EventTime).Skip((plan.Page - 1) * plan.Limit).Take(plan.Limit);
            table = "| وقت الحدث (الرياض) | العملية | المستخدم | رقم الإدخال | الاسم وقت الحدث | النتيجة المسجلة |\n| --- | --- | --- | --- | --- | --- |\n" +
                string.Join("\n", rows.Select(e => $"| {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(e.EventTime, "Asia/Riyadh"):yyyy-MM-dd HH:mm:ss} | {Cell(Label(e.EventType))} | {Cell(e.UserName)} | {e.EntryId?.ToString() ?? "—"} | {Cell(e.EntryName)} | {Cell(e.Outcome)} |")) +
                $"\n\n| إجمالي الأحداث المطابقة داخل التقرير | الصفحة |\n| --- | --- |\n| {selected.Length} | {plan.Page} |\n";
        }
        table += "\n\n| نطاق البيانات وحدودها | القيمة |\n| --- | --- |\n" +
            $"| المصدر | {Cell(data.ScopeDescription)} |\n| وقت اكتمال التصدير UTC | {data.ExportedAt:O} |\n" +
            "| حدود النتيجة | أحداث التقرير المحفوظ وفلاتره وفترة الاحتفاظ والتدقيق المفعّل فقط؛ لا تثبت حالة الحذف الحالية أو سلامة الملف الفعلي |\n";
        // Historical IDs must not turn into accessible-document links or follow-up document scopes.
        return new ChatResult(table, [], new AnswerScope("audit-saved-report", repositoryId,
            0, 0, false, data.ScopeDescription, []) { EventCount = selected.Length });
    }
}
