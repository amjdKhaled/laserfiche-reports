using System.Globalization;
using System.Text.RegularExpressions;

namespace LaserficheReports.Web;

internal sealed record RepositoryQuery
{
    public string Intent { get; init; } = "search";
    public string EntryType { get; init; } = "documents";
    public string? Field { get; init; }
    public string Operator { get; init; } = "equals";
    public string? Value { get; init; }
    public string? Template { get; init; }
    public string? Name { get; init; }
    public int? EntryId { get; init; }
    public int? FolderId { get; init; }
    public string? FolderName { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string DateField { get; init; } = "created";
    public string Sort { get; init; } = "creationTime desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string[] GroupBy { get; init; } = [];

    public void Validate()
    {
        if (Intent is not ("count" or "search" or "metadata" or "folder" or "templates" or "repository" or "report" or "unsupported"))
            throw new ArgumentException("نوع الطلب غير مدعوم.");
        if (EntryType is not ("documents" or "folders" or "entries")) throw new ArgumentException("نوع الإدخال غير مدعوم.");
        if (Operator != "equals") throw new ArgumentException("عامل المقارنة غير مدعوم؛ استخدم يساوي.");
        if (Page is < 1 or > 1000000 || PageSize is < 1 or > 100) throw new ArgumentException("رقم الصفحة أو حجمها غير صالح.");
        if (EntryId is <= 0 || FolderId is < 0) throw new ArgumentException("رقم الوثيقة أو المجلد غير صالح.");
        if (Intent == "metadata" && EntryId is null) throw new ArgumentException("حدد رقم الوثيقة لعرض بياناتها.");
        if ((StartDate is null) != (EndDate is null) || StartDate > EndDate) throw new ArgumentException("نطاق التاريخ غير صالح.");
        if (DateField is not ("created" or "modified")) throw new ArgumentException("نوع التاريخ غير صالح.");
        if (Sort is not ("creationTime desc" or "creationTime asc" or "lastModifiedTime desc" or "name asc" or "id asc"))
            throw new ArgumentException("الترتيب غير مدعوم.");
        if (GroupBy is null || GroupBy.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("قائمة حقول التجميع غير صالحة.");
        if (GroupBy.Length > 2) throw new ArgumentException("يمكن التجميع بحسب حقلين كحد أقصى.");
        foreach (var text in new[] { Field, Value, Template, Name, FolderName }.Concat(GroupBy))
            if (text is not null) SafeLiteral(text);
        if (Field is not null && Value is null) throw new ArgumentException("حدد قيمة الحقل المطلوبة.");
    }

    internal RepositoryQuery ForPage(int page)
    {
        var query = this with { Intent = Intent == "report" ? "search" : Intent, Page = page };
        query.Validate();
        return query;
    }

    // Reject LF syntax/wildcards inside model-controlled literals. Path backslashes
    // are handled separately after resolving an authoritative folder entry.
    internal static string SafeLiteral(string value)
    {
        if (value.Length > 256 || value.Any(c => char.IsControl(c) || "\"\\{}[]*?".Contains(c)))
            throw new ArgumentException("القيمة تحتوي رموز بحث غير مسموحة.");
        return value;
    }

    internal string Expression(string dateFormat)
    {
        Validate();
        var type = EntryType switch { "documents" => "D", "folders" => "F", _ => "DFS" };
        var parts = new List<string> { $"{{LF:Name=\"{(Name is null ? "*" : SafeLiteral(Name))}\", Type={type}}}" };
        if (EntryId is not null) parts.Add($"{{LF:ID={EntryId}}}");
        if (Field is not null) parts.Add($"{{[]:[{SafeLiteral(Field)}]=\"{SafeLiteral(Value!)}\"}}");
        if (Template is not null) parts.Add($"{{LF:Template=\"{SafeLiteral(Template)}\"}}");
        if (StartDate is not null)
        {
            var date = DateField == "created" ? "Created" : "Modified";
            parts.Add($"{{LF:{date}>=\"{StartDate.Value.ToString(dateFormat, CultureInfo.InvariantCulture)}\"}}");
            parts.Add($"{{LF:{date}<=\"{EndDate!.Value.ToString(dateFormat, CultureInfo.InvariantCulture)}\"}}");
        }
        return string.Join(" & ", parts);
    }
}

internal static class QuestionRouter
{
    internal static RepositoryQuery? TryRoute(string question, DateOnly today)
    {
        var q = question.Trim();
        var key = ReportSupport.MatchKey(q);
        var type = Regex.IsMatch(q, "مجلد|folders", RegexOptions.IgnoreCase) && !Regex.IsMatch(q, "وثائ|وثيق|documents", RegexOptions.IgnoreCase) ? "folders" : "documents";
        var count = Regex.IsMatch(q, @"(?:كم|عدد|\bcount\b|how many)", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(q, "(?:لخص|لخّص|محتوى|أهم النقاط|اهم النقاط|summari).*(?:وثيقة|وثائق|document)|(?:صورة|صفحات)", RegexOptions.IgnoreCase))
            return new() { Intent = "unsupported" };
        // Filters must be processed before broad inventory/count rules.
        var condition = ReportSupport.ParseCondition(q);
        if (condition is not null && !ReportSupport.NeedsFilterClarification(q))
            return new() { Intent = count ? "count" : q.Contains("تقرير") ? "report" : "search", EntryType = type, Field = condition.FieldQuestion, Value = condition.ExpectedValue };
        var natural = Regex.Match(q, @"(?:الحقل|حقل)?\s*(إجراء الوثيقة|اجراء الوثيقة|الحالة|حالة)\s*(?:فيها|فيه)?\s*(?:هو|هي)?\s*(تحت الإجراء|تحت الاجراء|مكتمل|مكتملة|مرفوض|مرفوضة)\s*[؟?]?$", RegexOptions.IgnoreCase);
        if (natural.Success) return new() { Intent = count ? "count" : q.Contains("تقرير") ? "report" : "search", EntryType = type, Field = natural.Groups[1].Value, Value = natural.Groups[2].Value };
        if (ReportSupport.IsFolderCountQuestion(q)) return new() { Intent = "count", EntryType = "folders" };
        if (ReportSupport.IsInventoryQuestion(q)) return new() { Intent = count ? "count" : "search" };
        if (key.Contains("القوالب") && !key.Contains("وثائق") && !key.Contains("عدد")) return new() { Intent = "templates" };
        var ids = ReportSupport.RequestedEntries(q);
        if (ids.Length == 1 && ReportSupport.IsDocumentMetadataQuestion(q)) return new() { Intent = "metadata", EntryId = ids[0] };
        if (key.Contains("اليوم") && Regex.IsMatch(q, "منش|مضاف|created", RegexOptions.IgnoreCase))
            return new() { StartDate = today, EndDate = today };
        if (key.Contains("هذاالاسبوع") && key.Contains("معدل"))
            return new() { DateField = "modified", StartDate = today.AddDays(-(int)today.DayOfWeek), EndDate = today, Sort = "lastModifiedTime desc" };
        var recent = Regex.Match(q, @"(?:آخر|اخر|last)\s*(\d+)?\s*(?:وثائق|وثيقة|documents)", RegexOptions.IgnoreCase);
        if (recent.Success) return new() { PageSize = recent.Groups[1].Success ? int.Parse(recent.Groups[1].Value, CultureInfo.InvariantCulture) : 10,
            Sort = "lastModifiedTime desc" };
        if (key.Contains("محتوى") || key.Contains("لخصالوثيقة") || key.Contains("الصورة") || key.Contains("الصفحات"))
            return new() { Intent = "unsupported" };
        return null;
    }
}
