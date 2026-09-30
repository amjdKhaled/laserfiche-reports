using System.Text;
using System.Text.RegularExpressions;

namespace LaserficheReports.Web;

internal static class ReportsQuestionScope
{
    public static bool IsWholeRepository(string question) =>
        Regex.IsMatch(question, @"وثائق|مستندات|ملفات", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(question, @"لخص|ملخص|أهم|اهم|جميع|\bكل\b|اذكر|أذكر|اعرض|أعرض|عدد|كم|ما\s*هي|ماهي", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(question, @"المفهرس|الفهرس|المخزن|المستودع|repo(?:sitory)?|جميع|\bكل\b", RegexOptions.IgnoreCase);
}

internal sealed record RepositorySummaryItem(int EntryId, string Name, string Category,
    string Summary, bool Indexed, bool HasPageText);

internal static class RepositorySummaryReport
{
    private static string Normalize(string name) =>
        Regex.Replace(name, @"\s+", "").Replace('إ', 'ا').Replace('أ', 'ا');

    public static string SummarizeFields(IEnumerable<(string Name, string? Value)> fields)
    {
        var populated = fields.Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .GroupBy(field => Normalize(field.Name))
            .Select(group => new
            {
                Key = group.Key, Name = group.First().Name.Trim(),
                Values = group.Select(field => field.Value!.Trim()).Distinct().ToArray()
            }).ToArray();
        var conflicts = populated.Where(field => field.Values.Length > 1)
            .Select(field => $"تعارض في {field.Name}: {string.Join(" / ", field.Values)}");
        var details = populated.Where(field => field.Values.Length == 1 &&
                Regex.IsMatch(field.Key, @"حالةالوثيقة|اجراءالوثيقة|نوعالوثيقة|موعد|تاريخ|التصنيفالرئيسي"))
            .OrderBy(field => Regex.IsMatch(field.Key, @"حالةالوثيقة|اجراءالوثيقة") ? 0 :
                field.Key.Contains("موعد") ? 1 : 2)
            .Take(3).Select(field => $"{field.Name}: {field.Values[0]}");
        var summary = string.Join("؛ ", conflicts.Concat(details));
        return summary.Length == 0 ? "لا تتوفر حقول وصفية كافية لتلخيص هذه الوثيقة." : summary;
    }

    private static string Line(string value) => value.Replace("\r", " ").Replace("\n", " ");

    public static string Render(IReadOnlyList<RepositorySummaryItem> documents, string question,
        int? discoveredCount = null)
    {
        if (documents.Count == 0) return discoveredCount > 0
            ? "تعذر قراءة الوثائق التي ظهرت أثناء الحصر؛ لا يمكن تقديم ملخص موثوق."
            : "لا توجد وثائق متاحة للقراءة في المستودع الحالي.";
        var indexed = documents.Count(document => document.Indexed);
        var pages = documents.Count(document => document.HasPageText);
        var text = new StringBuilder();
        if (discoveredCount > documents.Count)
            text.AppendLine($"الملخص غير مكتمل: ظهر {discoveredCount} وثيقة أثناء الحصر، وتعذر إدراج {discoveredCount - documents.Count} بعد تغير الوصول أو الحذف.");
        text.AppendLine($"راجعت {documents.Count} وثيقة متاحة في Laserfiche: {indexed} مفهرسة، و{documents.Count - indexed} غير مفهرسة.");
        text.AppendLine($"يتوفر نص صفحات مفهرس لـ {pages} وثيقة. الملخص أدناه لبيانات الوثائق الحالية؛ لا يتضمن تحليل نصوص الصفحات.");
        if (Regex.IsMatch(question, @"كم\s+(?:عدد|وثيق[ةه]|مستند|ملف)|عدد\s+(?:الوثائق|المستندات|الملفات)"))
            return text.ToString().TrimEnd();
        text.AppendLine();
        foreach (var group in documents.GroupBy(document =>
                     string.IsNullOrWhiteSpace(document.Category) ? "غير مصنف" : document.Category)
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key))
        {
            text.AppendLine($"{Line(group.Key)} — {group.Count()} وثيقة:");
            text.AppendLine();
            foreach (var document in group.OrderBy(document => document.EntryId))
            {
                var note = document.Indexed ? "" : " غير مفهرسة؛ لا يتوفر ملخص لمحتوى الصفحات.";
                text.AppendLine($"• {Line(document.Name)} — ID {document.EntryId}");
                text.AppendLine($"  {Line(document.Summary + note)}");
                text.AppendLine();
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }
}
