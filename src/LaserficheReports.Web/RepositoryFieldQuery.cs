using System.Text.RegularExpressions;

namespace LaserficheReports.Web;

internal sealed record RepositoryFieldQuery(string FieldPrefix, string Value)
{
    internal static string Normalize(string text) => Regex.Replace(text, @"[\s\p{Mn}ـ]+", "")
        .Replace('إ', 'ا').Replace('أ', 'ا').Replace('آ', 'ا').ToLowerInvariant();

    public static bool TryParse(string question, out RepositoryFieldQuery? query)
    {
        query = null;
        var match = Regex.Match(question, @"^(?<field>.+?)\s*(?:يساوي|تساوي|=)\s*(?<value>.+?)\s*[؟?]?$");
        if (!match.Success) return false;
        var value = match.Groups["value"].Value.Trim().Trim('"', '\'', '«', '»', '؟', '?');
        if (value.Length == 0) return false;
        query = new RepositoryFieldQuery(match.Groups["field"].Value.Trim(), value);
        return true;
    }

    public bool MatchesName(string fieldName) => !string.IsNullOrWhiteSpace(fieldName) &&
        Normalize(FieldPrefix).EndsWith(Normalize(fieldName), StringComparison.Ordinal);

    public bool MatchesValue(string? fieldValue) => !string.IsNullOrWhiteSpace(fieldValue) &&
        Normalize(Value) == Normalize(fieldValue);
}
