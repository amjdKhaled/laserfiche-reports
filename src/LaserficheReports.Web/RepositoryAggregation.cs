using System.Globalization;
using System.Text.Json;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Web;

internal static class RepositoryAggregation
{
    internal static string[] Values(LFSearchResult item, string field) => field switch
    {
        "entryId" => [item.EntryId.ToString(CultureInfo.InvariantCulture)], "name" => [item.Name],
        "created" => item.CreationTime is { } created ? [created.ToString("O")] : [],
        "modified" => item.LastModifiedTime is { } modified ? [modified.ToString("O")] : [],
        "template" => item.TemplateName is { Length: > 0 } template ? [template] : [],
        "creator" => item.Creator is { Length: > 0 } creator ? [creator] : [],
        "pageCount" => item.PageCount is { } pages ? [pages.ToString(CultureInfo.InvariantCulture)] : [],
        _ => item.Fields.Where(f => f.Name == field).SelectMany(f => f.Values).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray()
    };

    internal static void Validate(QueryPlan plan, IEnumerable<LFFieldDefinition> schema)
    {
        if ((plan.GroupFields?.Length ?? 0) > 4 || (plan.Metrics?.Length ?? 0) > 4) throw new ArgumentException("حدد حتى أربعة أبعاد ومقاييس.");
        foreach (var group in plan.GroupFields ?? [])
        {
            var type = StructuredRepositoryQuery.TypeOf(group.Field, schema);
            if (group.Bucket != null && (!StructuredRepositoryQuery.IsDate(type) || group.Bucket is not ("day" or "week" or "month" or "year")))
                throw new ArgumentException("التجميع الزمني يتطلب حقل تاريخ ووحدة صالحة.");
        }
        foreach (var metric in plan.Metrics ?? [])
        {
            if (metric.Function is not ("count" or "sum" or "average" or "min" or "max" or "distinct_count")) throw new ArgumentException("مقياس غير صالح.");
            if (metric.Function != "count" && metric.Field is null) throw new ArgumentException("المقياس يتطلب حقلًا.");
            if (metric.Field != null && !StructuredRepositoryQuery.IsNumber(StructuredRepositoryQuery.TypeOf(metric.Field, schema)) && metric.Function is not ("distinct_count" or "count"))
                throw new ArgumentException("الحساب يتطلب حقلًا رقميًا.");
        }
        if (plan.Rollup is not (null or "average" or "sum" or "min" or "max")) throw new ArgumentException("ملخص المقاييس غير صالح.");
        if (plan.Having is { } having && (having.Metric < 0 || having.Metric >= Math.Max(plan.Metrics?.Length ?? 0, 1) ||
            having.Operator is not ("equals" or "not_equals" or "greater_than" or "less_than" or "greater_or_equal" or "less_or_equal")))
            throw new ArgumentException("شرط المقياس غير صالح.");
        if (plan.SortField != null) StructuredRepositoryQuery.TypeOf(plan.SortField, schema);
        if (plan.AggregateSort is not (null or "metric asc" or "metric desc" or "group asc" or "group desc")) throw new ArgumentException("ترتيب التجميع غير صالح.");
    }

    internal static PagedResult<LFSearchResult> SortPage(PagedResult<LFSearchResult> result, QueryPlan plan, IEnumerable<LFFieldDefinition> schema)
    {
        if (result.HasNextPage || !result.IsTotalCountExact || result.TotalCount != result.Items.Count)
            throw new ArgumentException("تعذر تحميل النطاق الكامل لترتيبه بدقة.");
        var type = StructuredRepositoryQuery.TypeOf(plan.SortField!, schema);
        IComparable? Key(LFSearchResult item)
        {
            var values = Values(item, plan.SortField!);
            if (values.Length > 1) throw new ArgumentException("ترتيب حقل متعدد القيم يحتاج تحديد معيار.");
            if (values.Length == 0) return null;
            if (StructuredRepositoryQuery.IsNumber(type)) return decimal.Parse(values[0], CultureInfo.InvariantCulture);
            if (StructuredRepositoryQuery.IsDate(type)) return DateTimeOffset.Parse(values[0], CultureInfo.InvariantCulture);
            return values[0];
        }
        var sorted = plan.SortDirection == "desc" ? result.Items.OrderByDescending(Key) : result.Items.OrderBy(Key);
        if (plan.CompleteListing) return result with { Items = sorted.ToArray(), PageNumber = 1, HasMore = false };
        var items = sorted.Skip((plan.Page - 1) * plan.Limit).Take(plan.Limit).ToArray();
        return result with { Items = items, PageNumber = plan.Page, PageSize = plan.Limit, HasMore = plan.Page * plan.Limit < result.TotalCount };
    }

    internal static ChatResult Render(string repository, QueryPlan plan, PagedResult<LFSearchResult> result,
        IEnumerable<LFFieldDefinition> schema, IReadOnlyList<int> ids)
    {
        Validate(plan, schema);
        if (result.HasNextPage || !result.IsTotalCountExact || result.TotalCount != result.Items.Count)
            throw new ArgumentException("التجميع يحتاج كل النتائج الحية؛ لم أعرض حسابًا مبنيًا على عينة.");
        var dimensions = plan.GroupFields ?? [];
        var metrics = plan.Metrics is { Length: > 0 } requested ? requested : [new AggregateMetric("count")];
        string Dimension(LFSearchResult item, GroupDimension group)
        {
            var values = Values(item, group.Field);
            if (values.Length > 1) throw new ArgumentException("حقل التجميع متعدد القيم؛ حدد هل تُحسب الوثيقة لكل قيمة.");
            if (values.Length == 0) return "غير مذكور";
            if (group.Bucket == null) return values[0];
            if (!DateTimeOffset.TryParse(values[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new ArgumentException("قيمة تاريخ غير صالحة للتجميع.");
            date = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(date, "Asia/Riyadh");
            return group.Bucket switch { "day" => date.ToString("yyyy-MM-dd"), "week" => date.AddDays(-(int)date.DayOfWeek).ToString("yyyy-MM-dd"), "month" => date.ToString("yyyy-MM"), _ => date.ToString("yyyy") };
        }
        decimal? Calculate(IEnumerable<LFSearchResult> items, AggregateMetric metric)
        {
            if (metric.Function == "count") return metric.Field == null ? items.Count() : items.Count(i => Values(i, metric.Field).Length > 0);
            var values = items.SelectMany(i => Values(i, metric.Field!)).ToArray();
            if (metric.Function == "distinct_count") return values.Distinct(StringComparer.Ordinal).Count();
            if (values.Length == 0) return null; // Missing is never zero.
            var numbers = values.Select(v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n :
                throw new ArgumentException("قيمة غير رقمية في الحساب.")).ToArray();
            return metric.Function switch { "sum" => numbers.Sum(), "average" => numbers.Average(), "min" => numbers.Min(), _ => numbers.Max() };
        }
        var groups = result.Items.GroupBy(i => JsonSerializer.Serialize(dimensions.Select(g => Dimension(i, g)).ToArray()))
            .Select(g => new { Labels = JsonSerializer.Deserialize<string[]>(g.Key)!, Metrics = metrics.Select(m => Calculate(g, m)).ToArray() }).ToArray();
        if (plan.Having is { } condition)
            groups = groups.Where(g => g.Metrics[condition.Metric] is decimal value && (condition.Operator switch
            {
                "equals" => value == condition.Value, "not_equals" => value != condition.Value,
                "greater_than" => value > condition.Value, "less_than" => value < condition.Value,
                "greater_or_equal" => value >= condition.Value, _ => value <= condition.Value
            })).ToArray();
        var rollupValues = groups.Select(g => g.Metrics[0]).OfType<decimal>().ToArray();
        decimal? rollup = rollupValues.Length == 0 ? null : plan.Rollup switch
        {
            "average" => rollupValues.Average(), "sum" => rollupValues.Sum(), "min" => rollupValues.Min(),
            "max" => rollupValues.Max(), _ => null
        };
        var ordered = plan.AggregateSort switch
        {
            "metric asc" => groups.OrderBy(g => g.Metrics[0]), "group asc" => groups.OrderBy(g => string.Join(" / ", g.Labels)),
            "group desc" => groups.OrderByDescending(g => string.Join(" / ", g.Labels)), _ => groups.OrderByDescending(g => g.Metrics[0])
        };
        var shown = ordered.Skip((plan.Page - 1) * plan.Limit).Take(plan.Limit).ToArray();
        string Label(AggregateMetric m) => (m.Function switch { "count" => "العدد", "sum" => "المجموع", "average" => "المتوسط", "min" => "الأدنى", "max" => "الأعلى", _ => "عدد القيم المختلفة" }) + (m.Field == null ? "" : " — " + m.Field);
        var headers = dimensions.Select(g => g.Field + (g.Bucket == null ? "" : " / " + g.Bucket)).Concat(metrics.Select(Label)).ToArray();
        var detail = $"حُسبت النتائج من **{result.TotalCount}** إدخال حي. عدد المجموعات: {groups.Length}؛ المعروض: {shown.Length}.";
        if (plan.Rollup != null) detail += " ملخص المقياس الأول (" + plan.Rollup + "): **" + (rollup?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—") + "**؛ حُسب قبل تقسيم الصفحات.";
        var table = "# " + ReportSupport.Cell(plan.Title ?? "إحصاءات المستودع") + "\n\n" + detail + "\n\n| " + string.Join(" | ", headers.Select(ReportSupport.Cell)) + " |\n| " + string.Join(" | ", headers.Select(_ => "---")) + " |\n" +
            string.Join("\n", shown.Select(g => "| " + string.Join(" | ", g.Labels.Select(ReportSupport.Cell).Concat(g.Metrics.Select(n => n?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—"))) + " |"));
        return new ChatResult(table, [], new AnswerScope("repository", repository, result.TotalCount, 0, true, detail, ids));
    }
}
