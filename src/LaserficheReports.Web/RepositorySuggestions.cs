namespace LaserficheReports.Web;

// Suggestions are labels to confirm, never substitute query predicates.
internal static class RepositorySuggestions
{
    private static string[] Tokens(string value) => System.Text.RegularExpressions.Regex.Matches(value, @"[\p{L}\p{N}]+").Cast<System.Text.RegularExpressions.Match>()
        .Select(m => ReportSupport.MatchKey(m.Value)).Where(t => t.Length > 1).ToArray();

    internal static string[] Closest(string text, IEnumerable<string> names)
    {
        var query = Tokens(text);
        return names.Distinct(StringComparer.Ordinal).Select(name =>
        {
            var tokens = Tokens(name);
            var score = tokens.Length == 0 ? 0 : tokens.Count(t => query.Contains(t)) / (double)tokens.Length;
            return new { Name = name, Score = score, Length = tokens.Length };
        }).Where(x => x.Score >= .5).OrderByDescending(x => x.Score).ThenByDescending(x => x.Length)
          .ThenBy(x => x.Name, StringComparer.Ordinal).Take(3).Select(x => x.Name).ToArray();
    }

    internal static string MissingFolder(string requested, IEnumerable<string> fields)
    {
        var candidates = Closest(requested, fields);
        return "لم أجد مجلدًا مطابقًا للاسم «" + requested + "». " + (candidates.Length > 0
            ? "هل تقصد أحد الحقول التالية بدلًا من المجلد؟\n" + string.Join("\n", candidates.Select((n, i) => $"{i + 1}. الحقل «{n}»")) + "\nاختر اسم الحقل، أو اذكر اسم المجلد الصحيح أو رقمه."
            : "هل تقصد مجلدًا أم حقلًا؟ اذكر اسم الحقل، أو اسم المجلد الصحيح أو رقمه.")
            + "\nلم أغيّر نطاق البحث ولم أعتبر هذا عددًا يساوي صفرًا.";
    }
}
