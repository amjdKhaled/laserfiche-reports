using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using Npgsql;

namespace LaserficheReports.Web;

internal sealed record Evidence(int EntryId, string DocumentName, string Path, int? PageNumber,
    float Similarity, string Text, string TextSource);
internal sealed record ChatResult(string Answer, IReadOnlyList<Evidence> Sources);
internal sealed record IndexedDocument(int EntryId, string Name, string Path, string Status,
    int ChunkCount, string? TextSource);
internal sealed record IndexedDocumentPage(IReadOnlyList<IndexedDocument> Items, int Page, bool HasMore);
internal sealed record IndexOverviewDocument(int EntryId, string Name, string Path,
    string Template, string Status, string TextSource, IReadOnlyList<string> Highlights);

/// <summary>Retrieves project-owned rows and verifies each document against the live repository.</summary>
internal sealed class ReportsChatService(
    IConfiguration configuration,
    ITextEmbeddingService embeddings,
    IRepositoryContext repositories,
    ILaserficheEntryService entries,
    IHttpClientFactory clients)
{
    private static readonly JsonSerializerOptions GraphJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private string ConnectionString => configuration["Supabase:PostgresConnectionString"]
        ?? throw new InvalidOperationException("Supabase:PostgresConnectionString is missing.");

    public async Task<IndexedDocumentPage> ListAsync(int page, string? search, CancellationToken cancellationToken)
    {
        if (page < 1 || page > 1_000_000) throw new ArgumentOutOfRangeException(nameof(page));
        if (search?.Length > 200) throw new ArgumentException("Search is too long.", nameof(search));
        var repository = await repositories.GetActiveRepositoryAsync(cancellationToken);
        var result = new List<IndexedDocument>();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            select metadata ->> 'entry_id', metadata ->> 'document_name',
                   metadata ->> 'full_path', metadata ->> 'ingestion_status',
                   metadata ->> 'chunk_count', metadata ->> 'text_source'
            from public.documents
            where metadata ->> 'source' = 'laserfiche-reports'
              and metadata ->> 'record_type' = 'document-metadata'
              and lower(metadata ->> 'repository_id') = lower(@repository)
              and (@search is null or metadata ->> 'document_name' ilike @search
                   or metadata ->> 'full_path' ilike @search
                   or (metadata -> 'fields')::text ilike @search)
            order by id desc limit 51 offset @offset
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("repository", repository.RepositoryId);
        command.Parameters.Add(new NpgsqlParameter("search", NpgsqlTypes.NpgsqlDbType.Text)
            { Value = string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{search.Trim()}%" });
        command.Parameters.AddWithValue("offset", (page - 1) * 50);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<IndexedDocument>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!int.TryParse(reader.GetString(0), out var entryId)) continue;
            candidates.Add(new IndexedDocument(entryId, reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) || !int.TryParse(reader.GetString(4), out var count) ? 0 : count,
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        // Never expose an indexed document that the active repository credential cannot read.
        foreach (var candidate in candidates.Take(50))
        {
            if (await CanReadAsync(candidate.EntryId, cancellationToken)) result.Add(candidate);
        }
        return new IndexedDocumentPage(result, page, candidates.Count > 50);
    }

    public async Task<ChatResult> AskAsync(string question, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
            throw new ArgumentException("Question must contain 1 to 2000 characters.", nameof(question));

        var repository = await repositories.GetActiveRepositoryAsync(cancellationToken);
        // An explicit document number narrows evidence before vector ranking.
        var requestedEntry = Regex.Match(question, @"(?:وثيق[ةه]|مستند|entry|#)\s*(?:رقم\s*)?#?\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var entryFilter = requestedEntry.Success ? requestedEntry.Groups[1].Value : null;
        if (entryFilter is null && IsWholeIndexQuestion(question))
            return await AnswerIndexOverviewAsync(repository.RepositoryId, question, cancellationToken);
        var prefix = configuration["LocalAI:QueryEmbeddingPrefix"] ?? "search_query: ";
        var vector = (await embeddings.CreateEmbeddingsAsync(
            [prefix + question.Trim()], cancellationToken))[0];
        var literal = "[" + string.Join(",", vector.Select(v =>
            v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
        var candidates = new List<Evidence>();
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            const string sql = """
                select content, metadata,
                       (1 - (embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector)))::real as similarity
                from public.documents
                where embedding is not null
                  and metadata ->> 'source' = 'laserfiche-reports'
                  and lower(metadata ->> 'repository_id') = lower(@repository)
                  and metadata ->> 'record_type' = 'document-chunk'
                  and (@entryId is null or metadata ->> 'entry_id' = @entryId)
                  and (@entryId is not null or
                       (1 - (embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector))) >= 0.25)
                order by case when @entryId is not null and metadata ->> 'text_source' = 'laserfiche-metadata'
                              then 0 else 1 end,
                         embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector)
                limit 64
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("embedding", literal);
            command.Parameters.AddWithValue("repository", repository.RepositoryId);
            command.Parameters.Add(new NpgsqlParameter("entryId", NpgsqlTypes.NpgsqlDbType.Text)
                { Value = (object?)entryFilter ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                using var metadata = JsonDocument.Parse(reader.GetString(1));
                var root = metadata.RootElement;
                if (!root.TryGetProperty("entry_id", out var id) ||
                    id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var entryId))
                    continue;
                int? page = root.TryGetProperty("page_number", out var p) &&
                    p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pageValue)
                    ? pageValue : null;
                candidates.Add(new Evidence(entryId,
                    root.TryGetProperty("document_name", out var name) ? name.GetString() ?? "" : "",
                    root.TryGetProperty("full_path", out var path) ? path.GetString() ?? "" : "",
                    page, reader.GetFloat(2), reader.GetString(0),
                    root.TryGetProperty("text_source", out var source) ? source.GetString() ?? "" : ""));
            }
        }

        var authorized = new HashSet<int>();
        var denied = new HashSet<int>();
        var evidence = new List<Evidence>();
        var perDocument = new Dictionary<int, int>();
        var maxPerDocument = entryFilter is null ? 1 : 8;
        foreach (var candidate in candidates)
        {
            if (!authorized.Contains(candidate.EntryId) && !denied.Contains(candidate.EntryId))
            {
                if (await CanReadAsync(candidate.EntryId, cancellationToken)) authorized.Add(candidate.EntryId);
                else denied.Add(candidate.EntryId);
            }
            if (authorized.Contains(candidate.EntryId) && evidence.Count < 8 &&
                perDocument.GetValueOrDefault(candidate.EntryId) < maxPerDocument)
            {
                evidence.Add(candidate);
                perDocument[candidate.EntryId] = perDocument.GetValueOrDefault(candidate.EntryId) + 1;
            }
        }
        if (evidence.Count == 0)
            return new ChatResult("لم أجد معلومات كافية في الوثائق المفهرسة للإجابة عن هذا السؤال.", evidence);

        var client = clients.CreateClient("ReportsGraph");
        // Send only the fields used by the graph; repository paths can be very long.
        var graphEvidence = evidence.Select(item => new
        {
            item.EntryId,
            DocumentName = item.DocumentName[..Math.Min(item.DocumentName.Length, 256)],
            item.PageNumber,
            item.TextSource,
            Text = item.Text[..Math.Min(item.Text.Length, 2500)]
        }).ToArray();
        // StringContent computes Content-Length; Python's local HTTP server needs
        // request framing to read the JSON body.
        var graphRequest = JsonSerializer.Serialize(
            new { question = question.Trim(), evidence = graphEvidence }, GraphJsonOptions);
        using var content = new StringContent(graphRequest, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("answer", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Local LangGraph returned HTTP {(int)response.StatusCode}. Check the LangGraph terminal and Ollama model.");
        var graphResponse = await response.Content.ReadFromJsonAsync<GraphAnswer>(cancellationToken);
        if (string.IsNullOrWhiteSpace(graphResponse?.Answer))
            throw new InvalidOperationException("The local LangGraph service returned an empty answer.");
        return new ChatResult(graphResponse.Answer, evidence);
    }

    private static bool IsWholeIndexQuestion(string question) =>
        Regex.IsMatch(question,
            @"(?:الوثائق|المستندات)\s+(?:المفهرس[ةه]|الموجودة\s+في\s+الفهرس)|(?:وثيق[ةه]|مستند)\s+مفهرس[ةه]",
            RegexOptions.CultureInvariant) &&
        Regex.IsMatch(question, @"لخص|ملخص|أهم|اهم|جميع|كل|اذكر|اعرض|عدد|كم|ما\s*هي|ماهي",
            RegexOptions.CultureInvariant);

    private async Task<ChatResult> AnswerIndexOverviewAsync(string repositoryId, string question,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select distinct on (metadata ->> 'entry_id')
                   metadata ->> 'entry_id', metadata ->> 'document_name',
                   metadata ->> 'full_path', metadata ->> 'template_name',
                   metadata ->> 'ingestion_status', metadata ->> 'text_source',
                   coalesce((metadata -> 'fields')::text, '[]')
            from public.documents
            where metadata ->> 'source' = 'laserfiche-reports'
              and metadata ->> 'record_type' = 'document-metadata'
              and lower(metadata ->> 'repository_id') = lower(@repository)
            order by metadata ->> 'entry_id', id desc
            limit 1001
            """;
        var candidates = new List<IndexOverviewDocument>();
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("repository", repositoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!int.TryParse(reader.GetString(0), out var entryId)) continue;
                string Read(int index) => reader.IsDBNull(index) ? "" : reader.GetString(index);
                candidates.Add(new IndexOverviewDocument(entryId, Read(1), Read(2), Read(3),
                    Read(4), Read(5), ReadHighlights(Read(6))));
            }
        }

        var visible = new List<IndexOverviewDocument>();
        foreach (var candidate in candidates.Take(1000))
        {
            if (await CanReadAsync(candidate.EntryId, cancellationToken)) visible.Add(candidate);
        }
        visible.Sort((left, right) => left.EntryId.CompareTo(right.EntryId));
        if (visible.Count == 0)
            return new ChatResult("لا توجد وثائق مفهرسة متاحة للقراءة في المستودع الحالي.", []);

        var countOnly = Regex.IsMatch(question, @"كم\s+(?:عدد|وثيق[ةه]|مستند)|عدد\s+(?:الوثائق|المستندات)",
            RegexOptions.CultureInvariant);
        var more = candidates.Count > 1000;
        if (countOnly)
            return new ChatResult(more
                ? $"توجد أكثر من {visible.Count} وثيقة مفهرسة متاحة لك. افتح قسم الوثائق لاستعراض البقية."
                : $"عدد الوثائق المفهرسة المتاحة لك في المستودع الحالي: {visible.Count} وثيقة.", []);

        var summary = new StringBuilder();
        summary.AppendLine(more
            ? $"ملخص بيانات أول {visible.Count} وثيقة مفهرسة متاحة لك (يوجد المزيد في قسم الوثائق):"
            : $"ملخص بيانات جميع الوثائق المفهرسة المتاحة لك ({visible.Count} وثيقة):");
        var contentCount = visible.Count(document => document.Status == "content-indexed");
        var metadataCount = visible.Count(document => document.Status == "metadata-indexed");
        summary.AppendLine($"- تحتوي {contentCount} وثيقة على محتوى صفحات مفهرس، " +
            $"و{metadataCount} وثيقة على بيانات Laserfiche فقط.");
        if (visible.Count > contentCount + metadataCount)
            summary.AppendLine($"- {visible.Count - contentCount - metadataCount} وثيقة بحالة فهرسة أخرى.");
        var templates = visible.Where(document => !string.IsNullOrWhiteSpace(document.Template))
            .GroupBy(document => document.Template).OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key).Take(8);
        var templateSummary = string.Join("؛ ", templates.Select(group => $"{group.Key} ({group.Count()})"));
        if (templateSummary.Length > 0) summary.AppendLine("- أبرز القوالب: " + templateSummary + ".");
        summary.AppendLine("النقاط أدناه مستخرجة من أسماء الوثائق وحقول Laserfiche المفهرسة.");
        summary.AppendLine();
        summary.AppendLine("الوثائق وأبرز بياناتها:");
        foreach (var document in visible)
        {
            summary.Append($"- {document.Name} — ID {document.EntryId}");
            if (document.Highlights.Count > 0)
                summary.Append(" | " + string.Join("؛ ", document.Highlights));
            summary.AppendLine();
        }
        var sources = visible.Select(document => new Evidence(document.EntryId, document.Name,
            document.Path, null, 1, string.Join("\n", document.Highlights), "laserfiche-metadata"))
            .ToArray();
        return new ChatResult(summary.ToString().TrimEnd(), sources);
    }

    private static IReadOnlyList<string> ReadHighlights(string json)
    {
        using var fields = JsonDocument.Parse(json);
        if (fields.RootElement.ValueKind != JsonValueKind.Array) return [];
        return fields.RootElement.EnumerateArray()
            .Where(field => field.ValueKind == JsonValueKind.Object &&
                field.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                field.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(field => (Name: field.GetProperty("name").GetString() ?? "",
                Value: field.GetProperty("value").GetString() ?? ""))
            .OrderBy(field => Regex.IsMatch(field.Name, "موضوع|اسم الوثيقة|التصنيف|نوع الوثيقة|حالة الوثيقة") ? 0 : 1)
            .Take(2)
            .Select(field => $"{field.Name}: {field.Value[..Math.Min(field.Value.Length, 120)]}")
            .ToArray();
    }

    private async Task<bool> CanReadAsync(int entryId, CancellationToken cancellationToken)
    {
        try
        {
            return (await entries.GetEntryAsync(entryId, cancellationToken)).EntryType == LFEntryType.Document;
        }
        catch (LaserficheException exception) when (exception.StatusCode is 403 or 404)
        {
            return false;
        }
        // Authentication failures and service outages must fail the entire request.
    }

    private sealed record GraphAnswer(string Answer);
}
