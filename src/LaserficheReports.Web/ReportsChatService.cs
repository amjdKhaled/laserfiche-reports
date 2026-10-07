using System.Net.Http.Json;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using Npgsql;

namespace LaserficheReports.Web;

internal sealed record Evidence(int EntryId, string DocumentName, string Path, int? PageNumber,
    float Similarity, string Text, string TextSource);
internal sealed record ChatResult(string Answer, IReadOnlyList<Evidence> Sources, AnswerScope? Scope = null)
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public ReportQuality? Quality { get; init; }
    public int[] RelatedEntryIds { get; init; } = [];
    public IReadOnlyList<ReportSection> Reports { get; init; } = [];
}
internal sealed record ReportSection(string Title, string Answer, IReadOnlyList<Evidence> Sources, AnswerScope? Scope,
    int[] RelatedEntryIds, ReportQuality? Quality);
internal sealed record ReportQuality(string Status, bool QuoteVerification, string SemanticReview,
    string PromptVersion, int ModelCalls);
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
    LiveRepositoryReportService liveReports,
    QuestionRouter router, ILaserficheSearchService searches, ILogger<ReportsChatService> logger)
{
    private string ConnectionString => configuration["Supabase:PostgresConnectionString"]
        ?? throw new InvalidOperationException("Supabase:PostgresConnectionString is missing.");

    public async Task<IndexedDocumentPage> ListAsync(int page, string? search, CancellationToken cancellationToken)
    {
        if (page < 1 || page > 1_000_000) throw new ArgumentOutOfRangeException(nameof(page));
        if (search?.Length > 200) throw new ArgumentException("Search is too long.", nameof(search));
        var repository = await repositories.GetActiveRepositoryAsync(cancellationToken);
        var expression = LiveRepositoryReportService.Documents;
        if (!string.IsNullOrWhiteSpace(search))
            expression += $" & {{LF:Name=\"{LiveRepositoryReportService.Term(search.Trim())}\", Type=D}}";
        var live = await searches.QueryAsync(expression, page, 50, cancellationToken: cancellationToken);
        var chunks = new Dictionary<int, int>();
        try
        {
            using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readBudget.CancelAfter(TimeSpan.FromSeconds(5));
            await using var connection = new NpgsqlConnection(OcrConnectionString());
            await connection.OpenAsync(readBudget.Token);
            await using var command = new NpgsqlCommand("""
                select metadata ->> 'entry_id', count(*)::int from public.documents
                where metadata ->> 'source' = 'laserfiche-reports'
                  and metadata ->> 'record_type' = 'document-chunk'
                  and coalesce(metadata ->> 'text_source', '') not like 'laserfiche-metadata%'
                  and lower(metadata ->> 'repository_id') = lower(@repository)
                  and metadata ->> 'entry_id' = any(@ids)
                group by metadata ->> 'entry_id'
                """, connection) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("repository", repository.RepositoryId);
            command.Parameters.AddWithValue("ids", live.Items.Select(i => i.EntryId.ToString()).ToArray());
            await using var reader = await command.ExecuteReaderAsync(readBudget.Token);
            while (await reader.ReadAsync(readBudget.Token))
                if (int.TryParse(reader.GetString(0), out var id)) chunks[id] = reader.GetInt32(1);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is NpgsqlException or OperationCanceledException or InvalidOperationException or ArgumentException)
        { logger.LogWarning("OCR availability lookup failed ErrorType={ErrorType}", ex.GetType().Name); }
        return new IndexedDocumentPage(live.Items.Select(item => new IndexedDocument(item.EntryId,
            item.Name, item.FullPath, chunks.ContainsKey(item.EntryId) ? "content-indexed" : "محتوى غير متاح",
            chunks.GetValueOrDefault(item.EntryId), null)).ToArray(), page, live.HasNextPage);
    }

    public async Task<ChatResult> AskAsync(string question, CancellationToken cancellationToken, ChatTurn[]? history = null)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
            throw new ArgumentException("Question must contain 1 to 2000 characters.", nameof(question));

        var repository = await repositories.GetActiveRepositoryAsync(cancellationToken);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestTimeout = configuration.GetValue<int?>("Reports:RequestTimeoutSeconds") ?? 0;
        if (requestTimeout > 0) budget.CancelAfter(TimeSpan.FromSeconds(requestTimeout));
        cancellationToken = budget.Token;
        if (history is { Length: > 8 } || (history?.Any(t => t is null || t.Role is not ("user" or "assistant") || t.Text is null || t.Text.Length > 3000) ?? false))
            throw new ArgumentException("سياق المحادثة أكبر من الحد المسموح.");
        var schemaWatch = System.Diagnostics.Stopwatch.StartNew();
        var catalog = await liveReports.CatalogAsync(cancellationToken);
        logger.LogInformation("Stage=SCHEMA DurationMs={DurationMs}", schemaWatch.ElapsedMilliseconds);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var request = await router.RouteAsync(question, catalog, cancellationToken, history);
        logger.LogInformation("Stage=AI_PLAN ReportCount={Count} DurationMs={DurationMs}", request.Reports.Length, watch.ElapsedMilliseconds);
        if (request.Reports.All(p => p.Operation == "clarify"))
            return new ChatResult(request.Clarification ?? "وضح المعلومة المطلوبة أو معيار التقرير.", []);
        var sections = new List<ReportSection>();
        foreach (var plan in request.Reports)
        {
            ChatResult result;
            try { result = plan.Operation == "clarify"
                ? new ChatResult(request.Clarification ?? "وضح معيار هذا التقرير.", [])
                : await ExecutePlanAsync(repository, plan, plan.Question ?? question, cancellationToken); }
            catch (RepositoryScopeClarificationException clarification)
            {
                result = new ChatResult(clarification.Message, []);
            }
            catch (Exception error) when (request.Reports.Length > 1 && !cancellationToken.IsCancellationRequested &&
                error is LaserficheException or NpgsqlException or HttpRequestException or InvalidOperationException or ArgumentException)
            {
                logger.LogWarning("Report section failed Operation={Operation} ErrorType={ErrorType} Reason={Reason}", plan.Operation, error.GetType().Name,
                    error is ArgumentException ? error.Message : "upstream failure");
                result = new ChatResult(error is ArgumentException ? error.Message : "تعذر إكمال هذا التقرير؛ لم تُعرض نتائج أو أعداد غير مؤكدة.", []);
            }
            sections.Add(new ReportSection(plan.Title ?? "تقرير", result.Answer, result.Sources, result.Scope,
                result.RelatedEntryIds, result.Quality));
        }
        // Structured facts, counts and tables need no second model call.
        var allSources = new List<Evidence>();
        var combined = new List<string>();
        foreach (var section in sections)
        {
            var offset = allSources.Count;
            combined.Add("## " + ReportSupport.Cell(section.Title) + "\n\n" +
                System.Text.RegularExpressions.Regex.Replace(section.Answer, @"\[(\d+)\]", m =>
                    int.TryParse(m.Groups[1].Value, out var id) && id <= section.Sources.Count
                        ? "[" + (offset + id) + "]" : m.Value));
            allSources.AddRange(section.Sources);
        }
        return new ChatResult(string.Join("\n\n", combined), allSources, sections.Count == 1 ? sections[0].Scope : null)
        { Reports = sections, RelatedEntryIds = sections.SelectMany(s => s.RelatedEntryIds).Distinct().ToArray() };
    }

    private async Task<ChatResult> ExecutePlanAsync(RepositoryDescriptor repository, QueryPlan plan, string question,
        CancellationToken cancellationToken)
    {
        var requestedEntries = plan.EntryIds ?? [];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (plan.Operation == "clarify")
            return new ChatResult("حدد اسم الحقل وقيمته كما تظهر في Laserfiche، أو رقم الوثيقة المطلوب تحليلها.", []);
        if (plan.Operation is not ("search" or "folders" or "metadata" or "templates" or "schema" or "folder_information" or "recent" or "latest_created" or "latest_modified" or "created" or "modified" or "group" or "content"))
            throw new ArgumentException("لم أتعرف على الطلب. حدد الحقل وقيمته أو رقم الوثيقة والمعلومة المطلوبة.");
        if (plan.Name != null && (plan.Operation == "metadata" || plan.RequireUnique || plan.Content))
        {
            var lookup = await liveReports.SelectAsync(plan with { Operation = "search", Content = false, Limit = 50, CountOnly = false }, requestedEntries, false, cancellationToken);
            if (!lookup.IsTotalCountExact || lookup.TotalCount != 1)
                return new ChatResult("لم أحدد وثيقة واحدة بالاسم؛ اختر رقم الوثيقة من النتائج أو وضح الاسم.\n\n" + string.Join("\n", lookup.Items.Select(i => $"- {i.EntryId}: {ReportSupport.Cell(i.Name)}")), []);
            requestedEntries = [lookup.Items[0].EntryId];
            plan = plan with { Name = null };
        }
        if (!plan.Content)
            return await liveReports.CreateAsync(repository.RepositoryId, plan, requestedEntries, cancellationToken);
        var liveSelected = new Dictionary<int, LFEntry>();
        if (plan.Kind == QueryKind.HYBRID_QUERY)
        {
            var selected = await liveReports.SelectAsync(plan, requestedEntries, true, cancellationToken);
            if (selected.HasNextPage) throw new ArgumentException("حدد نطاقًا أضيق لتحليل محتوى الوثائق.");
            foreach (var item in selected.Items.Where(i => i.EntryType == LFEntryType.Document)) liveSelected[item.EntryId] = new LFEntry { Id = item.EntryId, Name = item.Name, FullPath = item.FullPath, EntryType = item.EntryType };
            requestedEntries = selected.Items.Select(i => i.EntryId).Distinct().ToArray();
            if (requestedEntries.Length == 0)
                return new ChatResult("لم يتم العثور على نتائج مطابقة.", []);
        }
        var hasEntryFilter = requestedEntries.Length > 0;
        var candidateLimit = Math.Clamp(configuration.GetValue<int?>("Reports:CandidateLimit") ?? 240, 24, 1000);
        var evidenceLimit = Math.Clamp(configuration.GetValue<int?>("Reports:EvidenceLimit") ?? 24, 8, 32);
        var prefix = configuration["LocalAI:QueryEmbeddingPrefix"] ?? "search_query: ";
        string? literal = null;
        watch.Restart();
        var rankedContent = !hasEntryFilter || plan.ContentMode == "search";
        if (rankedContent) try
        {
            using var embeddingBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            embeddingBudget.CancelAfter(TimeSpan.FromSeconds(120));
            var vectors = await embeddings.CreateEmbeddingsAsync([prefix + question.Trim()], embeddingBudget.Token);
            if (vectors.Count == 0 || vectors[0].Length == 0 || vectors[0].Any(v => !float.IsFinite(v)))
                throw new InvalidOperationException("The local embedding model returned an invalid vector.");
            literal = "[" + string.Join(",", vectors[0].Select(v =>
                v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // Lexical retrieval remains usable during an embedding outage; expose this in scope.
        }
        logger.LogInformation("Stage=EMBEDDING DurationMs={DurationMs} Used={Used}", watch.ElapsedMilliseconds, literal is not null);
        var keywords = HybridRetrieval.KeywordQuery(question);
        var candidates = new List<Evidence>();
        watch.Restart();
        await using (var connection = new NpgsqlConnection(OcrConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(rankedContent ? HybridRetrieval.Sql : DirectOcrSql, connection);
            command.CommandTimeout = Math.Clamp(configuration.GetValue<int?>("Reports:SearchTimeoutSeconds") ?? 20, 5, 30);
            command.Parameters.Add(new NpgsqlParameter("embedding", NpgsqlTypes.NpgsqlDbType.Text)
                { Value = (object?)literal ?? DBNull.Value });
            command.Parameters.AddWithValue("hasVector", literal is not null);
            command.Parameters.AddWithValue("keywords", keywords);
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

        logger.LogInformation("Stage=SUPABASE Tool=GetOcrContent DurationMs={DurationMs} Passages={Passages}", watch.ElapsedMilliseconds, candidates.Count);
        var authorized = liveSelected;
        var candidateIds = candidates.Select(c => c.EntryId).Distinct().Where(id => !authorized.ContainsKey(id)).Take(evidenceLimit).ToArray();
        var currentEntries = new LFEntry?[candidateIds.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, candidateIds.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (index, token) =>
        {
            try { currentEntries[index] = await entries.GetEntryAsync(candidateIds[index], token); }
            catch (LaserficheException error) when (error.StatusCode is 403 or 404) { }
        });
        foreach (var current in currentEntries.OfType<LFEntry>().Where(e => e.EntryType == LFEntryType.Document)) authorized[current.Id] = current;
        var allowedCandidates = candidates.Where(c => authorized.ContainsKey(c.EntryId))
            .Select(c => c with { DocumentName = authorized[c.EntryId].Name, Path = authorized[c.EntryId].FullPath }).ToList();
        var evidence = ReportSupport.SelectEvidence(allowedCandidates, evidenceLimit);
        var scope = new AnswerScope(hasEntryFilter ? "selected-documents" : "repository",
            repository.RepositoryId, evidence.Select(x => x.EntryId).Distinct().Count(), evidence.Count, false,
            hasEntryFilter ? "التحليل مقيد بالوثائق التي حددتها؛ يعتمد على المقاطع المفهرسة المتاحة منها."
                : "البحث شمل فهرس المستودع المتاح؛ المقاطع المختارة أدلة للإجابة وليست حصرًا لجميع الوثائق.",
            plan.Kind == QueryKind.HYBRID_QUERY ? [] : requestedEntries);
        if (literal is null && rankedContent)
            scope = scope with { Detail = scope.Detail + " البحث بالكلمات فقط؛ تعذر استخدام نموذج البحث الدلالي المحلي." };
        if (evidence.Count == 0)
            return new ChatResult("# تقرير البحث\n\nمحتوى الوثيقة غير متاح حاليًا، أو لا توجد مقاطع نصية كافية للإجابة.\n\n" + scope.Detail, evidence, scope);

        if (candidates.Count >= candidateLimit || evidence.Count < allowedCandidates.Count)
            scope = scope with { Detail = scope.Detail + " التحليل يعتمد على مقاطع مختارة؛ لم تُراجع جميع الصفحات." };
        watch.Restart();
        var client = clients.CreateClient("ReportsGraph");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Request-ID", System.Diagnostics.Activity.Current?.TraceId.ToString());
        // ByteArrayContent advertises the actual UTF-8 length for Python HTTP framing.
        using var body = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            new { question = question.Trim(), evidence, scope }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await client.PostAsync("answer", body, cancellationToken);
        await GraphServiceException.EnsureSuccessAsync(response, "answer", cancellationToken);
        logger.LogInformation("Stage=AI Tool=Answer DurationMs={DurationMs} Status={Status}", watch.ElapsedMilliseconds, (int)response.StatusCode);
        var graphResponse = await response.Content.ReadFromJsonAsync<GraphAnswer>(cancellationToken);
        if (string.IsNullOrWhiteSpace(graphResponse?.Answer))
            throw new InvalidOperationException("The local LangGraph service returned an empty answer.");
        return new ChatResult(graphResponse.Answer + ReportSupport.SourceTable(evidence), evidence, scope)
            { Quality = graphResponse.Quality, RelatedEntryIds = (graphResponse.RelatedEntryIds ?? [])
                .Where(id => evidence.Any(e => e.EntryId == id)).Distinct().ToArray() };
    }

    private string OcrConnectionString()
    {
        var options = new NpgsqlConnectionStringBuilder(ConnectionString) { Timeout = 10, CommandTimeout = 20 };
        return options.ConnectionString;
    }

    // Entry identities constrain the lookup; stored names/fields are never a current-data source.
    private const string DirectOcrSql = """
        select content, metadata, 1::real
        from public.documents
        where metadata ->> 'source' = 'laserfiche-reports'
          and metadata ->> 'record_type' = 'document-chunk'
          and coalesce(metadata ->> 'text_source', '') not like 'laserfiche-metadata%'
          and lower(metadata ->> 'repository_id') = lower(@repository)
          and metadata ->> 'entry_id' = any(@entryIds)
        order by row_number() over (partition by metadata ->> 'entry_id' order by id), id
        limit @candidateLimit
        """;

    private sealed record GraphAnswer(string Answer, ReportQuality? Quality, int[]? RelatedEntryIds);
}
