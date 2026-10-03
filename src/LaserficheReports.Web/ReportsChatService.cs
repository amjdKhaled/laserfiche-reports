using System.Net.Http.Json;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using Npgsql;

namespace LaserficheReports.Web;

internal sealed record Evidence(int EntryId, string DocumentName, string Path, int? PageNumber,
    float Similarity, string Text, string TextSource);
internal sealed record ChatResult(string Answer, IReadOnlyList<Evidence> Sources, AnswerScope? Scope = null)
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
internal sealed record IndexedDocument(int EntryId, string Name, string Path, string Status,
    int ChunkCount, string? TextSource);
internal sealed record IndexedDocumentPage(IReadOnlyList<IndexedDocument> Items, int Page, bool HasMore);

/// <summary>Retrieves project-owned rows and verifies each document against the live repository.</summary>
internal sealed class ReportsChatService(
    IConfiguration configuration,
    ITextEmbeddingService embeddings,
    IRepositoryContext repositories,
    ILaserficheEntryService entries,
    IHttpClientFactory clients,
    LiveRepositoryReportService liveReports)
{
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
        var requestedEntries = ReportSupport.RequestedEntries(question);
        if (requestedEntries.Length > 50)
            throw new ArgumentException("حدد حتى 50 وثيقة في السؤال الواحد.", nameof(question));
        if (ReportSupport.NeedsFilterClarification(question))
            return new ChatResult("# توضيح شروط التقرير\n\nالطلب يتضمن أكثر من شرط أو مقارنة غير مدعومة في فحص الحقول الحالي. " +
                "اكتب شرطًا واحدًا بهذه الصيغة: «إجراء الوثيقة يساوي تحت الاجراء». لن أعرض عددًا أو قائمة على أنها حصر مؤكد لهذا الطلب.", []);
        var condition = ReportSupport.ParseCondition(question);
        if (condition is not null || ReportSupport.IsInventoryQuestion(question))
            return await liveReports.CreateAsync(repository.RepositoryId, condition, requestedEntries, cancellationToken);
        var hasEntryFilter = requestedEntries.Length > 0;
        var candidateLimit = Math.Clamp(configuration.GetValue<int?>("Reports:CandidateLimit") ?? 240, 24, 1000);
        var evidenceLimit = Math.Clamp(configuration.GetValue<int?>("Reports:EvidenceLimit") ?? 24, 8, 32);
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
                  and (not @hasEntryFilter or metadata ->> 'entry_id' = any(@entryIds))
                  and (@hasEntryFilter or
                       (1 - (embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector))) >= 0.25)
                order by case when @hasEntryFilter and metadata ->> 'text_source' = 'laserfiche-metadata'
                              then 0 else 1 end,
                         embedding OPERATOR(extensions.<=>) cast(@embedding as extensions.vector)
                limit @candidateLimit
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("embedding", literal);
            command.Parameters.AddWithValue("repository", repository.RepositoryId);
            command.Parameters.AddWithValue("hasEntryFilter", hasEntryFilter);
            command.Parameters.AddWithValue("entryIds", requestedEntries.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            command.Parameters.AddWithValue("candidateLimit", candidateLimit);
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
        var allowedCandidates = new List<Evidence>();
        foreach (var candidate in candidates)
        {
            if (!authorized.Contains(candidate.EntryId) && !denied.Contains(candidate.EntryId))
            {
                if (await CanReadAsync(candidate.EntryId, cancellationToken)) authorized.Add(candidate.EntryId);
                else denied.Add(candidate.EntryId);
            }
            if (authorized.Contains(candidate.EntryId)) allowedCandidates.Add(candidate);
        }
        var evidence = ReportSupport.SelectEvidence(allowedCandidates, evidenceLimit);
        var scope = new AnswerScope(hasEntryFilter ? "selected-documents" : "repository",
            repository.RepositoryId, evidence.Select(x => x.EntryId).Distinct().Count(), evidence.Count, false,
            hasEntryFilter ? "التحليل مقيد بالوثائق التي حددتها؛ يعتمد على المقاطع المفهرسة المتاحة منها."
                : "البحث شمل فهرس المستودع المتاح؛ المقاطع المختارة أدلة للإجابة وليست حصرًا لجميع الوثائق.",
            requestedEntries);
        if (evidence.Count == 0)
            return new ChatResult("# تقرير البحث\n\nلم أجد أدلة مفهرسة كافية للإجابة. تأكد من فهرسة محتوى الوثائق المطلوبة.\n\n" + scope.Detail, evidence, scope);

        var client = clients.CreateClient("ReportsGraph");
        using var response = await client.PostAsJsonAsync("answer",
            new { question = question.Trim(), evidence, scope }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Local LangGraph returned HTTP {(int)response.StatusCode}. Check the LangGraph terminal and Ollama model.");
        var graphResponse = await response.Content.ReadFromJsonAsync<GraphAnswer>(cancellationToken);
        if (string.IsNullOrWhiteSpace(graphResponse?.Answer))
            throw new InvalidOperationException("The local LangGraph service returned an empty answer.");
        return new ChatResult(graphResponse.Answer + ReportSupport.SourceTable(evidence), evidence, scope);
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
