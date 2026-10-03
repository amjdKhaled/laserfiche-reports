using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Web;

internal sealed record AnswerScope(string Mode, string RepositoryId, int DocumentCount,
    int EvidenceCount, bool Exhaustive, string Detail, IReadOnlyList<int> RequestedEntryIds);
internal sealed record FieldCondition(string FieldQuestion, string ExpectedValue);

internal static partial class ReportSupport
{
    // Only document-labelled IDs narrow the search. Dates and other numbers do not.
    internal static int[] RequestedEntries(string question)
    {
        var normalized = NormalizeDigits(question);
        var ids = new List<int>();
        foreach (Match match in EntryGroupRegex().Matches(normalized))
            foreach (Match number in Regex.Matches(match.Groups[1].Value, @"[0-9]+"))
                if (int.TryParse(number.Value, out var id) && id > 0 && !ids.Contains(id)) ids.Add(id);
        return ids.ToArray();
    }

    internal static FieldCondition? ParseCondition(string question)
    {
        var matches = EqualityRegex().Matches(question);
        if (matches.Count != 1) return null;
        var comparison = matches[0];
        var left = question[..comparison.Index].Trim();
        var value = question[(comparison.Index + comparison.Length)..].Trim()
            .TrimEnd('؟', '?', '،').Trim().Trim('"', '\'', '«', '»');
        return left.Length > 0 && value.Length is > 0 and <= 200
            ? new FieldCondition(left, value) : null;
    }

    internal static bool IsInventoryQuestion(string question) => InventoryRegex().IsMatch(
        Regex.Replace(question.Trim().TrimEnd('؟', '?', '.', '!'), @"\s+", " "));

    internal static bool NeedsFilterClarification(string question) =>
        EqualityRegex().Matches(question).Count > 1 ||
        (EqualityRegex().IsMatch(question) && CompoundFilterRegex().IsMatch(question));

    // Ignore harmless Arabic hamza/diacritic/whitespace differences for matching
    // metadata, while preserving the original field name/value in the report.
    internal static string MatchKey(string value)
    {
        var output = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c) || c == '\u0640' ||
                CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            output.Append(c is 'أ' or 'إ' or 'آ' ? 'ا' : char.ToLowerInvariant(c));
        }
        return output.ToString();
    }

    internal static bool MatchesField(FieldCondition condition, string fieldName) =>
        !string.IsNullOrWhiteSpace(fieldName) &&
        MatchKey(condition.FieldQuestion.TrimEnd('"', '\'', '«', '»')).EndsWith(
            MatchKey(fieldName), StringComparison.Ordinal);

    internal static bool MatchesValue(string? actual, string expected, bool multiValue = false)
    {
        if (actual is null) return false;
        if (MatchKey(actual) == MatchKey(expected)) return true;
        // LF represents multi-values as comma-space-separated text. Only split
        // declared multi-values, avoiding substring matches (e.g. رفض vs غير مرفوض).
        return multiValue && actual.Split(", ", StringSplitOptions.TrimEntries)
            .Any(value => MatchKey(value) == MatchKey(expected));
    }

    internal static string Cell(string? value) => string.IsNullOrWhiteSpace(value) ? "غير مذكور" :
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            .Replace("<", "‹", StringComparison.Ordinal).Replace(">", "›", StringComparison.Ordinal);

    internal static string SourceTable(IReadOnlyList<Evidence> evidence)
    {
        var result = new StringBuilder("\n\n## الوثائق والمصادر\n\n| المرجع | رقم الوثيقة | اسم الوثيقة | الصفحة / المصدر |\n| --- | --- | --- | --- |\n");
        for (var i = 0; i < evidence.Count; i++)
        {
            var item = evidence[i];
            var origin = item.TextSource.StartsWith("laserfiche-metadata", StringComparison.Ordinal)
                ? "حقول Laserfiche" : item.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "غير مذكورة";
            result.AppendLine($"| [{i + 1}] | {item.EntryId} | {Cell(item.DocumentName)} | {origin} |");
        }
        return result.ToString();
    }

    internal static IReadOnlyList<Evidence> SelectEvidence(IReadOnlyList<Evidence> candidates, int limit)
    {
        var groups = candidates.GroupBy(x => x.EntryId).Select(group => group
            .GroupBy(x => (x.PageNumber, x.TextSource, x.Text))
            .Select(x => x.First()).ToArray()).ToArray();
        var output = new List<Evidence>();
        // Round-robin: avoid one document consuming the entire answer context.
        for (var depth = 0; output.Count < limit && groups.Any(g => g.Length > depth); depth++)
            foreach (var group in groups)
            {
                if (group.Length > depth) output.Add(group[depth]);
                if (output.Count == limit) break;
            }
        return output;
    }

    private static string NormalizeDigits(string value) => string.Concat(value.Select(c =>
    {
        var digit = CharUnicodeInfo.GetDigitValue(c);
        return digit >= 0 ? (char)('0' + digit) : c;
    }));

    [GeneratedRegex(@"(?:الوثائق|وثائق|المستندات|مستندات|وثيق[ةه]|مستند|documents?|entries|entry|#)\s*(?:(?:رقم|ارقام|أرقام|number|numbers|IDs?)\s*)?[#:]?\s*([0-9]+(?:\s*(?:[,،]|و|and)\s*[0-9]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EntryGroupRegex();
    [GeneratedRegex(@"\s*(?:يساوي|تساوي|قيمته|قيمتها|equals|=)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EqualityRegex();
    [GeneratedRegex(@"^(?:(?:اعرض|اذكر|اعطني|أعطني|اعطيني|أعطيني|وريني|طلع|ابغا|أبغا|ابي|أبي|أريد|اريد|قائمة|تقرير عن|ما هي|ماهي|ايش|وش|ما|كم عدد|عدد)\s+)?(?:(?:لي|تقرير|قائمة|بكل|عن|بجميع)\s+)*(?:جميع\s+|كل\s+)?(?:الوثائق|المستندات|الملفات)\s*(?:(?:الموجود[ةه]?|المتاحة)\s*)?(?:في\s*(?:(?:هذا|هذي|كل|جميع)\s+)?(?:المستودع|مستودع|المخزن|المخزن هذا|الريبو|(?:ال\s*)?(?:repasetory|repository|repo)))?$|^(?:كم\s+(?:وثيقة|مستند|ملف)\s+في\s+(?:هذا\s+)?(?:المستودع|المخزن)|(?:list|show|count)\s+(?:me\s+)?(?:all\s+)?documents(?:\s+(?:in|from)\s+(?:this\s+|the\s+)?repository)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InventoryRegex();
    [GeneratedRegex(@"(?:\s+(?:و|أو|او|and|or)\s+[^؟?]*?(?:الحقل|حقل|التصنيف|موعد|تاريخ|اجراء|إجراء)|(?:>=|<=|!=|≠)|(?:أكبر من|اصغر من|أصغر من|اقل من|أقل من|قبل تاريخ|بعد تاريخ))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompoundFilterRegex();
}
