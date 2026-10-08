using System.Globalization;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Web;

internal sealed record RelativeDate(string Anchor = "today", string Unit = "day", int Offset = 0, string Boundary = "start");
internal sealed record CalendarPeriod(int Year, int? Month = null, int? Day = null)
{
    internal (DateOnly Start, DateOnly End) Bounds()
    {
        if (Year is < 1 or > 9998 || Month is < 1 or > 12 || Day is < 1 or > 31 || Day != null && Month == null)
            throw new ArgumentException("فترة تقويمية غير صالحة.");
        try
        {
            var start = new DateOnly(Year, Month ?? 1, Day ?? 1);
            return (start, Day != null ? start.AddDays(1) : Month != null ? start.AddMonths(1) : start.AddYears(1));
        }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("فترة تقويمية غير صالحة."); }
    }
}
internal sealed record RepositoryFilter(string? Field = null, string? Operator = null, string? Value = null,
    string? Upper = null, RelativeDate? Relative = null, RelativeDate? UpperRelative = null,
    string? Logic = null, RepositoryFilter[]? Conditions = null, string? Tag = null, CalendarPeriod? Period = null);
internal sealed record GroupDimension(string Field, string? Bucket = null);
internal sealed record AggregateMetric(string Function, string? Field = null);
internal sealed record AggregateHaving(int Metric, string Operator, decimal Value);

internal static class RepositoryDates
{
    internal static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
        DateTimeOffset.UtcNow, "Asia/Riyadh").DateTime);

    internal static DateOnly Resolve(RelativeDate value, DateOnly today)
    {
        if (value.Anchor != "today" || value.Offset is < -1200 or > 1200 ||
            value.Boundary is not ("start" or "end" or "rolling"))
            throw new ArgumentException("شرط التاريخ النسبي غير صالح.");
        var start = value.Unit switch
        {
            "day" => today.AddDays(value.Offset),
            "week" => (value.Boundary == "rolling" ? today : today.AddDays(-(int)today.DayOfWeek)).AddDays(value.Offset * 7),
            "month" => (value.Boundary == "rolling" ? today : new DateOnly(today.Year, today.Month, 1)).AddMonths(value.Offset),
            "year" => (value.Boundary == "rolling" ? today : new DateOnly(today.Year, 1, 1)).AddYears(value.Offset),
            _ => throw new ArgumentException("وحدة التاريخ غير صالحة.")
        };
        return value.Boundary != "end" ? start : value.Unit switch
        {
            "day" => start.AddDays(1), "week" => start.AddDays(7),
            "month" => start.AddMonths(1), _ => start.AddYears(1)
        };
    }
}

/// <summary>Only schema-verified data becomes Laserfiche syntax. No natural-language routing here.</summary>
internal static class StructuredRepositoryQuery
{
    internal static readonly string[] Builtins = ["entryId", "name", "created", "modified", "template", "creator", "pageCount"];
    internal static LFFieldDefinition ResolveField(string name, IEnumerable<LFFieldDefinition> schema)
    {
        var exact = schema.Where(f => f.Name == name).ToArray();
        var matches = exact.Length > 0 ? exact : schema.Where(f => ReportSupport.MatchKey(f.Name) == ReportSupport.MatchKey(name)).ToArray();
        if (matches.Length == 0 || matches.Select(f => (f.Name, f.FieldType)).Distinct().Count() != 1)
            throw new ArgumentException("الحقل غير موجود أو ملتبس في المستودع الحالي: " + name);
        return matches[0];
    }

    internal static string TypeOf(string field, IEnumerable<LFFieldDefinition> schema) => field switch
    {
        "entryId" or "pageCount" => "Integer", "created" or "modified" => "DateTime",
        "name" or "template" or "creator" => "String", _ => ResolveField(field, schema).FieldType
    };
    internal static bool IsDate(string type) => type.Equals("Date", StringComparison.OrdinalIgnoreCase) || type.Equals("DateTime", StringComparison.OrdinalIgnoreCase);
    internal static bool IsNumber(string type) => new[] { "Integer", "LongInteger", "Number", "Decimal", "Double", "ShortInteger", "Long", "Short" }.Contains(type, StringComparer.OrdinalIgnoreCase);

    internal static string Compile(RepositoryFilter filter, IEnumerable<LFFieldDefinition> schema, DateOnly today, int depth = 0, IEnumerable<string>? tags = null)
    {
        if (depth > 5) throw new ArgumentException("الفلاتر متداخلة أكثر من الحد المسموح.");
        if (filter.Conditions is { } conditions)
        {
            if (filter.Logic is not ("and" or "or") || conditions.Length is < 1 or > 20 || filter.Field != null || filter.Operator != null ||
                filter.Value != null || filter.Upper != null || filter.Relative != null || filter.UpperRelative != null || filter.Tag != null || filter.Period != null)
                throw new ArgumentException("مجموعة شروط غير صالحة.");
            return "(" + string.Join(filter.Logic == "and" ? " & " : " | ", conditions.Select(c => Compile(c, schema, today, depth + 1, tags))) + ")";
        }
        if (filter.Tag is not null)
        {
            if (filter.Operator is not ("has_tag" or "not_tag") || filter.Field != null || filter.Logic != null ||
                filter.Value != null || filter.Upper != null || filter.Relative != null || filter.UpperRelative != null || filter.Period != null)
                throw new ArgumentException("شرط الوسم غير صالح.");
            if (tags is null || !tags.Contains(filter.Tag, StringComparer.Ordinal))
                throw new ArgumentException("الوسم غير موجود في تعريفات المستودع الحالية: " + filter.Tag);
            var clause = $"{{LF:Tags=\"{LiveRepositoryReportService.Term(filter.Tag)}\"}}";
            return filter.Operator == "has_tag" ? clause : $"({{LF:Name=\"*\", Type=DF}} - {clause})";
        }
        if (filter.Field is null || filter.Logic != null) throw new ArgumentException("حدد حقل الشرط.");
        var field = filter.Field;
        var type = TypeOf(field, schema);
        var name = Builtins.Contains(field) ? field : ResolveField(field, schema).Name;
        var key = field switch { "entryId" => "ID", "name" => "Name", "created" => "Created", "modified" => "Modified", "template" => "TemplateName", "creator" => "Creator", "pageCount" => "PageCount", _ => null };
        string Clause(string op, string value) => key is null
            ? $"{{[]:[{LiveRepositoryReportService.Term(name, true)}]{op}\"{value}\"}}"
            : $"{{LF:{key}{op}{(field is "entryId" or "pageCount" ? value : "\"" + value + "\"")}}}";
        if (filter.Period != null || filter.Operator is "in_period" or "before_period" or "through_period" or "from_period" or "after_period")
        {
            if (!IsDate(type) || filter.Period == null || filter.Value != null || filter.Upper != null || filter.Relative != null || filter.UpperRelative != null)
                throw new ArgumentException("الفترة التقويمية تتطلب حقل تاريخ دون حدود إضافية.");
            var (start, end) = filter.Period.Bounds();
            var periodStart = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var periodEnd = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return filter.Operator switch
            {
                "in_period" => "(" + Clause(">=", periodStart) + " & " + Clause("<", periodEnd) + ")",
                "before_period" => Clause("<", periodStart), "through_period" => Clause("<", periodEnd),
                "from_period" => Clause(">=", periodStart), "after_period" => Clause(">=", periodEnd),
                _ => throw new ArgumentException("معامل الفترة غير صالح.")
            };
        }
        string Value(string? literal, RelativeDate? relative)
        {
            if (literal != null && relative != null) throw new ArgumentException("لا تجمع تاريخًا صريحًا ونسبيًا في نفس القيمة.");
            if (relative != null)
            {
                if (!IsDate(type)) throw new ArgumentException("التاريخ النسبي يتطلب حقل تاريخ.");
                return RepositoryDates.Resolve(relative, today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (literal is null) throw new ArgumentException("قيمة الشرط غير متاحة.");
            if (IsDate(type))
            {
                if (!DateOnly.TryParseExact(literal, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new ArgumentException("تاريخ غير صالح؛ استخدم yyyy-MM-dd.");
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (IsNumber(type))
            {
                if (!decimal.TryParse(literal, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
                    (type.Contains("Integer", StringComparison.OrdinalIgnoreCase) && number != decimal.Truncate(number)))
                    throw new ArgumentException("قيمة رقمية غير صالحة.");
                return number.ToString(CultureInfo.InvariantCulture);
            }
            return LiveRepositoryReportService.Term(literal);
        }
        if (filter.Operator is "is_empty" or "is_not_empty")
        {
            if (key != null || filter.Value != null || filter.Relative != null || filter.Upper != null || filter.UpperRelative != null)
                throw new ArgumentException("فحص الفراغ يتطلب حقل Metadata فقط دون قيمة.");
            return Clause(filter.Operator == "is_empty" ? "=" : "<>", "");
        }
        var first = Value(filter.Value, filter.Relative);
        var op = filter.Operator switch
        {
            "equals" => "=", "not_equals" => "<>", "greater_than" or "date_after" => ">",
            "less_than" or "date_before" => "<", "greater_or_equal" => ">=", "less_or_equal" => "<=",
            "contains" or "starts_with" => "=", "between" or "date_between" => ">=",
            _ => throw new ArgumentException("معامل الشرط غير صالح.")
        };
        if (op is not ("=" or "<>") && !IsDate(type) && !IsNumber(type))
            throw new ArgumentException("المقارنة تتطلب حقل تاريخ أو رقم.");
        if (filter.Operator is "contains" or "starts_with")
        {
            if (IsDate(type) || IsNumber(type)) throw new ArgumentException("البحث النصي يتطلب حقلًا نصيًا.");
            first = (filter.Operator == "contains" ? "*" : "") + first + "*";
        }
        if (filter.Operator is "between" or "date_between")
        {
            var upper = Value(filter.Upper, filter.UpperRelative);
            if ((IsNumber(type) ? decimal.Parse(first, CultureInfo.InvariantCulture) > decimal.Parse(upper, CultureInfo.InvariantCulture) : string.CompareOrdinal(first, upper) > 0))
                throw new ArgumentException("حدود النطاق معكوسة.");
            return "(" + Clause(">=", first) + " & " + Clause("<=", upper) + ")";
        }
        if (filter.Upper != null || filter.UpperRelative != null) throw new ArgumentException("حد علوي دون شرط نطاق.");
        return Clause(op, first);
    }
}
