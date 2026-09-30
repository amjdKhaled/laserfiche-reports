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
    string Template, string Status, string TextSource, string Category, IReadOnlyList<string> Highlights);
internal sealed record IndexSummaryExample(int EntryId, string DocumentName, string Detail);
internal sealed record IndexSummaryGroup(string Category, int Count, IReadOnlyList<IndexSummaryExample> Examples);

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
        if (entryFilter is null && TryGetListedFolder(question, out var folder))
            return await ListFolderDocumentsAsync(repository.RepositoryId, folder, cancellationToken);
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
        var coverage = entryFilter is null
            ? $"نطاق الإجابة: {evidence.Select(item => item.EntryId).Distinct().Count()} وثائق مسترجعة من الفهرس؛ هذه نتائج بحث ولا تمثل حصرًا لكل المستودع.\n\n"
            : "";
        return new ChatResult(coverage + graphResponse.Answer, evidence);
    }

    private static bool IsWholeIndexQuestion(string question) =>
        ReportsQuestionScope.IsWholeRepository(question);

    private static bool TryGetListedFolder(string question, out string folder)
    {
        folder = "";
        if (!Regex.IsMatch(question, @"وثائق|مستندات|ملفات|الملفات|الوثائق|المستندات",
                RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(question, @"اعط|أعط|اذكر|أذكر|اعرض|أعرض|اسرد|قائمة|ما\s*هي|ماهي|الموجود[ةه]|في|داخل",
                RegexOptions.CultureInvariant))
            return false;

        var match = Regex.Match(question,
            @"(?:مجلد|قسم|فولدر)\s+(?:الـ?\s*)?(?<folder>[\p{L}\p{N}_-]+)|(?:في|داخل|ضمن)\s+(?:الـ?\s*)?(?<folder>[A-Za-z][A-Za-z0-9_-]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        folder = match.Groups["folder"].Value;
        return folder.Length is > 0 and <= 80;
    }

    private async Task<ChatResult> ListFolderDocumentsAsync(string repositoryId, string folder,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select distinct metadata ->> 'entry_id'
            from public.documents
            where metadata ->> 'source' = 'laserfiche-reports'
              and metadata ->> 'record_type' = 'document-metadata'
              and lower(metadata ->> 'repository_id') = lower(@repository)
            """;
        var indexedIds = new List<int>();
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("repository", repositoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0) && int.TryParse(reader.GetString(0), out var entryId))
                    indexedIds.Add(entryId);
            }
        }

        var visible = new List<LFEntry>();
        var parentNames = new Dictionary<int, string>();
        foreach (var entryId in indexedIds.Order())
        {
            LFEntry document;
            try
            {
                document = await entries.GetEntryAsync(entryId, cancellationToken);
            }
            catch (LaserficheException exception) when (exception.StatusCode is 403 or 404)
            {
                continue;
            }
            if (document.EntryType != LFEntryType.Document) continue;

            var parentName = document.FolderPath.Split(['/', '\\'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(parentName) && document.ParentId > 0)
            {
                if (!parentNames.TryGetValue(document.ParentId, out parentName))
                {
                    try
                    {
                        var parent = await entries.GetEntryAsync(document.ParentId, cancellationToken);
                        parentName = parent.EntryType == LFEntryType.Folder ? parent.Name : "";
                    }
                    catch (LaserficheException exception) when (exception.StatusCode is 403 or 404)
                    {
                        parentName = "";
                    }
                    parentNames[document.ParentId] = parentName;
                }
            }
            if (string.IsNullOrWhiteSpace(parentName))
            {
                var lastSeparator = document.FullPath.LastIndexOfAny(['/', '\\']);
                if (lastSeparator > 0)
                    parentName = document.FullPath[..lastSeparator].Split(['/', '\\'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            }
            if (string.Equals(parentName, folder, StringComparison.OrdinalIgnoreCase))
                visible.Add(document);
        }
        if (visible.Count == 0)
            return new ChatResult($"لا توجد وثائق مفهرسة متاحة لك داخل مجلد {folder}.", []);

        var answer = new StringBuilder($"الوثائق المفهرسة داخل مجلد {folder} ({visible.Count}):\n");
        for (var index = 0; index < visible.Count; index++)
            answer.AppendLine($"{index + 1}. {visible[index].Name} — ID {visible[index].Id}");
        return new ChatResult(answer.ToString().TrimEnd(), []);
    }

    private async Task<ChatResult> AnswerIndexOverviewAsync(string repositoryId, string question,
        CancellationToken cancellationToken)
    {
        // Counts and coverage come from a complete live folder traversal, never Top-K.
        var currentDocuments = new Dictionary<int, LFEntry>();
        var pendingFolders = new Queue<int>();
        var visitedFolders = new HashSet<int>();
        pendingFolders.Enqueue(await entries.GetRootEntryIdAsync(cancellationToken));
        while (pendingFolders.TryDequeue(out var folderId))
        {
            if (!visitedFolders.Add(folderId)) continue;
            var children = await entries.GetAllFolderChildrenAsync(folderId, cancellationToken);
            foreach (var child in children)
            {
                if (child.EntryType == LFEntryType.Folder) pendingFolders.Enqueue(child.Id);
                else if (child.EntryType == LFEntryType.Document) currentDocuments[child.Id] = child;
            }
        }

        var indexStatuses = new Dictionary<int, string>();
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            const string sql = """
                select distinct on (metadata ->> 'entry_id')
                       metadata ->> 'entry_id', metadata ->> 'ingestion_status'
                from public.documents
                where metadata ->> 'source' = 'laserfiche-reports'
                  and metadata ->> 'record_type' = 'document-metadata'
                  and lower(metadata ->> 'repository_id') = lower(@repository)
                order by metadata ->> 'entry_id', id desc
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("repository", repositoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0) && int.TryParse(reader.GetString(0), out var entryId))
                    indexStatuses[entryId] = reader.IsDBNull(1) ? "" : reader.GetString(1);
            }
        }

        var documents = new List<RepositorySummaryItem>();
        foreach (var entryId in currentDocuments.Keys.Order())
        {
            LFEntry document;
            IReadOnlyList<LFFieldValue> fields;
            try
            {
                document = await entries.GetEntryAsync(entryId, cancellationToken);
                if (document.EntryType != LFEntryType.Document) continue;
                fields = await entries.GetEntryFieldsAsync(entryId, cancellationToken);
            }
            catch (LaserficheException exception) when (exception.StatusCode is 403 or 404)
            {
                // Permission changes or deletions during the scan cannot become evidence.
                continue;
            }
            var (category, _) = ReadFields(JsonSerializer.Serialize(fields.Select(field =>
                new { name = field.FieldName, value = field.Value })));
            var detail = RepositorySummaryReport.SummarizeFields(fields
                .Select(field => (field.FieldName, field.Value)));
            documents.Add(new RepositorySummaryItem(document.Id, document.Name, category, detail,
                indexStatuses.ContainsKey(document.Id),
                indexStatuses.GetValueOrDefault(document.Id) == "content-indexed"));
        }
        return new ChatResult(RepositorySummaryReport.Render(documents, question, currentDocuments.Count), []);
    }

    private static (string Category, IReadOnlyList<string> Highlights) ReadFields(string json)
    {
        using var fields = JsonDocument.Parse(json);
        if (fields.RootElement.ValueKind != JsonValueKind.Array) return ("", []);
        var populated = fields.RootElement.EnumerateArray()
            .Where(field => field.ValueKind == JsonValueKind.Object &&
                field.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                field.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(field => (Name: field.GetProperty("name").GetString() ?? "",
                Value: field.GetProperty("value").GetString() ?? ""))
            .DistinctBy(field => (Regex.Replace(field.Name, @"\s+", ""), field.Value.Trim()))
            .ToArray();
        var category = populated.FirstOrDefault(field =>
            Regex.Replace(field.Name, @"\s+", "").Contains("التصنيفالرئيسي")).Value ?? "";
        var highlights = populated
            .Where(field => !Regex.IsMatch(Regex.Replace(field.Name, @"\s+", ""),
                "اسمالوثيقة|التصنيفالرئيسي"))
            .OrderBy(field => Regex.IsMatch(field.Name, "نوع الوثيقة|حالة الوثيقة|موعد|تاريخ|الإدارة") ? 0 : 1)
            .Take(2)
            .Select(field => $"{Clip(field.Name, 80)}: {Clip(field.Value, 120)}")
            .ToArray();
        return (category, highlights);
    }

    private static string Clip(string value, int max) => value[..Math.Min(value.Length, max)];

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
