namespace LaserficheReports.Infrastructure.Services;

internal static class PageTextChunker
{
    internal static IReadOnlyList<TextChunk> Split(
        IReadOnlyList<LaserficheDocumentIngestionService.IndexedPageText> pages,
        int chunkSize,
        int overlap)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0 || overlap >= chunkSize) throw new ArgumentOutOfRangeException(nameof(overlap));

        var chunks = new List<TextChunk>();
        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            var text = Normalize(page.Text);
            if (text.Length == 0) continue;

            var start = 0;
            while (start < text.Length)
            {
                var idealEnd = Math.Min(start + chunkSize, text.Length);
                var end = FindNaturalBoundary(text, start, idealEnd);
                if (end <= start) end = idealEnd;

                var content = text[start..end].Trim();
                if (content.Length > 0)
                {
                    chunks.Add(new TextChunk(
                        chunks.Count,
                        page.PageNumber,
                        start,
                        end,
                        page.Source,
                        content));
                }

                if (end >= text.Length) break;
                start = Math.Max(end - overlap, start + 1);
                while (start < end && char.IsWhiteSpace(text[start])) start++;
            }
        }

        return chunks;
    }

    private static int FindNaturalBoundary(string text, int start, int idealEnd)
    {
        if (idealEnd >= text.Length) return text.Length;

        var minimumEnd = start + ((idealEnd - start) * 2 / 3);
        for (var index = idealEnd; index >= minimumEnd; index--)
        {
            if (index < text.Length && IsBoundary(text[index - 1], text[index]))
                return index;
        }

        return idealEnd;
    }

    private static bool IsBoundary(char previous, char current) =>
        previous is '\n' or '.' or '!' or '?' or '\u061F' or '\u06D4' || char.IsWhiteSpace(current);

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    internal sealed record TextChunk(
        int Index,
        int PageNumber,
        int StartOffset,
        int EndOffset,
        string Source,
        string Content);
}
