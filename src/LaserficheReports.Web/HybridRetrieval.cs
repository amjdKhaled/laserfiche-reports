using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LaserficheReports.Web;

/// <summary>Lexical and vector candidates share the same repository/document scope.
/// RRF scores are ranking signals, never confidence percentages. No schema changes.</summary>
internal static partial class HybridRetrieval
{
    private static readonly HashSet<string> StopWords = new(
        "ما ماهي هي هو هل من في عن على الى كيف ايش وش اريد ابغا ابي اعرض اذكر لخص تقرير الوثيقة الوثائق المستند المستندات ملف الملفات the a an is are of in on for what which show document documents report id".Split(' '),
        StringComparer.Ordinal);

    internal static string Normalize(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (c == '\u0640' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var digit = CharUnicodeInfo.GetDigitValue(c);
            output.Append(digit >= 0 ? (char)('0' + digit) : c switch
            {
                'أ' or 'إ' or 'آ' => 'ا', 'ى' => 'ي', _ => char.ToLowerInvariant(c)
            });
        }
        return output.ToString();
    }

    internal static string KeywordQuery(string question) => string.Join(" | ",
        TokenRegex().Matches(Normalize(question)).Select(m => m.Value)
            .Where(token => !StopWords.Contains(token)).Distinct().Take(20)
            .Select(token => $"'{token}'"));

    [GeneratedRegex(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    // 'simple' retains Arabic tokens. Normalization changes search keys only;
    // returned content remains byte-for-byte source text for quotation checks.
    internal const string Sql = """
        with scoped as not materialized (
            select id, content, metadata, embedding
            from public.documents
            where metadata ->> 'source' = 'laserfiche-reports'
              and lower(metadata ->> 'repository_id') = lower(@repository)
              and metadata ->> 'record_type' = 'document-chunk'
              and (not @hasEntryFilter or metadata ->> 'entry_id' = any(@entryIds))
        ), semantic as (
            select id,
                   (1 - (embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector)))::real as similarity,
                   row_number() over (order by embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector), id) as rank
            from scoped
            where @hasVector and embedding is not null
              and (@hasEntryFilter or (1 - (embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector))) >= 0.25)
            order by embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector), id
            limit @candidateLimit
        ), lexical_scores as (
            select id, ts_rank_cd(tokens, to_tsquery('simple', @keywords)) as score
            from (
                select id, to_tsvector('simple', translate(
                    regexp_replace(coalesce(content, '') || ' ' || coalesce(metadata ->> 'document_name', ''),
                                   '[ً-ٰٟـ]', '', 'g'),
                    'أإآى٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹', 'اااي01234567890123456789')) as tokens
                from scoped
            ) normalized
            where @keywords <> '' and tokens @@ to_tsquery('simple', @keywords)
            order by score desc, id limit @candidateLimit
        ), lexical as (
            select id, row_number() over (order by score desc, id) as rank from lexical_scores
        ), fused as (
            select coalesce(s.id, l.id) as id, coalesce(s.similarity, 0)::real as similarity,
                   coalesce(1.0 / (60 + s.rank), 0) + coalesce(1.0 / (60 + l.rank), 0) as score
            from semantic s full join lexical l on l.id = s.id
        )
        select d.content, d.metadata, f.similarity
        from fused f join scoped d on d.id = f.id
        order by f.score desc, f.id limit @candidateLimit
        """;
}
