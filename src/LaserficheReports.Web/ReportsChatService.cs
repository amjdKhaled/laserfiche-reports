using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
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
        var requestedEntry = Regex.Match(question,
            @"(?:وثيق[ةه]|مستند|ملف|entry|ID|#)\s*(?:(?:رقم|ID)\s*)?#?\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int? entryId = requestedEntry.Success ? int.Parse(requestedEntry.Groups[1].Value) : null;
        var folder = TryGetListedFolder(question, out var listedFolder) ? listedFolder : null;
        var requestedName = Regex.Match(question,
            "(?:وثيقة|مستند|ملف)\\s+[\"«](?<name>[^\"»]+)[\"»]");
        var name = requestedName.Success ? requestedName.Groups["name"].Value : null;

        if (RepositoryFieldQuery.TryParse(question, out var fieldQuery))
            return await AnswerFieldQueryAsync(question, fieldQuery!, entryId, folder, name, cancellationToken);
        if (!Regex.IsMatch(question, @"لخص|ملخص|المتعلقة|تحتوي|تخص|عن") &&
            Regex.IsMatch(question, @"قائمة|أسماء|اسماء|(?:الوثائق|الملفات|المستندات)\s+(?:الموجود[ةه]|في|داخل)"))
        {
            var scope = await ReadScopeAsync(entryId, folder, name, cancellationToken);
            var list = new StringBuilder($"النطاق: {ScopeLabel(entryId, folder, name)}. عدد الوثائق المتاحة: {scope.Documents.Count}.\n\n");
            if (scope.DiscoveredCount != scope.Documents.Count)
                list.AppendLine("القائمة غير مكتملة بسبب تغير الوصول أو الحذف أثناء الحصر.");
            foreach (var document in scope.Documents)
                list.AppendLine($"• {document.Entry.Name} — ID {document.Entry.Id}");
            return new ChatResult(list.ToString().TrimEnd(), []);
        }
        if (entryId is null && folder is null && name is null && IsWholeIndexQuestion(question))
            return await AnswerIndexOverviewAsync(repository.RepositoryId, question, cancellationToken);
        // Every other question scans the entire live scope; there is no global Top-K cap.
        return await AnswerRepositoryQuestionAsync(repository.RepositoryId, question, entryId,
            folder, name, cancellationToken);
    }

    private sealed record ScopedDocument(LFEntry Entry, IReadOnlyList<LFFieldValue> Fields);
    private sealed record ScopeDocuments(IReadOnlyList<ScopedDocument> Documents, int DiscoveredCount);

    private async Task<ScopeDocuments> ReadScopeAsync(int? entryId, string? folder, string? name,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<int, LFEntry>();
        var folders = new Dictionary<int, LFEntry>();
        if (entryId.HasValue)
        {
            var entry = await entries.GetEntryAsync(entryId.Value, cancellationToken);
            if (entry.EntryType == LFEntryType.Document) candidates[entry.Id] = entry;
        }
        else
        {
            var pending = new Queue<int>();
            var visited = new HashSet<int>();
            pending.Enqueue(await entries.GetRootEntryIdAsync(cancellationToken));
            while (pending.TryDequeue(out var id))
            {
                if (!visited.Add(id)) continue;
                foreach (var child in await entries.GetAllFolderChildrenAsync(id, cancellationToken))
                {
                    if (child.EntryType == LFEntryType.Folder)
                    {
                        folders[child.Id] = child;
                        pending.Enqueue(child.Id);
                    }
                    else if (child.EntryType == LFEntryType.Document) candidates[child.Id] = child;
                }
            }
        }
        bool InFolder(LFEntry document)
        {
            if (folder is null) return true;
            var parentId = document.ParentId;
            var visited = new HashSet<int>();
            while (folders.TryGetValue(parentId, out var parent) && visited.Add(parentId))
            {
                if (parent.Name.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
                parentId = parent.ParentId;
            }
            return document.FolderPath.Split(['/', '\\'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(part => part.Equals(folder, StringComparison.OrdinalIgnoreCase));
        }
        var scoped = candidates.Values.Where(document => InFolder(document) &&
                (name is null || document.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(document => document.Id).ToArray();
        var visible = new List<ScopedDocument>();
        foreach (var candidate in scoped)
        {
            try
            {
                var current = await entries.GetEntryAsync(candidate.Id, cancellationToken);
                if (current.EntryType != LFEntryType.Document || !InFolder(current) ||
                    (name is not null && !current.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                var fields = await entries.GetEntryFieldsAsync(current.Id, cancellationToken);
                visible.Add(new ScopedDocument(current, fields));
            }
            catch (LaserficheException exception) when (exception.StatusCode is 403 or 404)
            {
                // Never silently label a partial scan as complete.
            }
        }
        return new ScopeDocuments(visible, scoped.Length);
    }

    private static string ScopeLabel(int? entryId, string? folder, string? name) =>
        entryId.HasValue ? $"الوثيقة ID {entryId}" : folder is not null ? $"مجلد {folder}" :
        name is not null ? $"الوثيقة {name}" : "كل المستودع المتاح لحسابك";

    private async Task<ChatResult> AnswerFieldQueryAsync(string question, RepositoryFieldQuery query,
        int? entryId, string? folder, string? name, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(entryId, folder, name, cancellationToken);
        var allFields = scope.Documents.SelectMany(document => document.Fields)
            .Where(field => query.MatchesName(field.FieldName)).ToArray();
        if (allFields.Length == 0)
            return new ChatResult($"لم أجد الحقل المطلوب في {ScopeLabel(entryId, folder, name)}. لا يمكن تأكيد قائمة المطابقات.", []);
        var longestName = allFields.Max(field => RepositoryFieldQuery.Normalize(field.FieldName).Length);
        var targetFields = allFields.Where(field =>
            RepositoryFieldQuery.Normalize(field.FieldName).Length == longestName).ToArray();
        var targetName = targetFields[0].FieldName;
        var targetKey = RepositoryFieldQuery.Normalize(targetName);
        var matches = scope.Documents.Where(document => document.Fields.Any(field =>
            RepositoryFieldQuery.Normalize(field.FieldName) == targetKey && query.MatchesValue(field.Value)))
            .ToArray();
        var answer = new StringBuilder($"النطاق: {ScopeLabel(entryId, folder, name)}. فحصت {scope.Documents.Count} وثيقة من بيانات Laserfiche الحالية.\n");
        if (scope.Documents.Count != scope.DiscoveredCount)
            answer.AppendLine($"القائمة غير مكتملة: تعذر فحص {scope.DiscoveredCount - scope.Documents.Count} وثيقة ظهرت أثناء الحصر.");
        answer.AppendLine($"عدد المطابقات للحقل «{targetName}» = «{query.Value}»: {matches.Length}.\n");
        foreach (var document in matches)
        {
            answer.AppendLine($"• {document.Entry.Name} — ID {document.Entry.Id}");
            var values = document.Fields.Where(field =>
                    RepositoryFieldQuery.Normalize(field.FieldName) == targetKey)
                .Select(field => field.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
            answer.AppendLine($"  {targetName}: {string.Join(" / ", values)}");
            if (values.Select(value => RepositoryFieldQuery.Normalize(value!)).Distinct().Count() > 1)
                answer.AppendLine("  يوجد تعارض في قيم هذا الحقل؛ يلزم مراجعته.");
        }
        return new ChatResult(answer.ToString().TrimEnd(), []);
    }

    private sealed record VerifiedMatch(int SourceIndex, bool Relevant, string[] Quotes);
    private sealed record VerifiedMatches(VerifiedMatch[] Matches);

    private async Task<ChatResult> AnswerRepositoryQuestionAsync(string repositoryId, string question,
        int? entryId, string? folder, string? name, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(entryId, folder, name, cancellationToken);
        if (scope.Documents.Count == 0)
            return new ChatResult("لم أجد وثائق متاحة ضمن النطاق المحدد.", []);
        var live = scope.Documents.ToDictionary(document => document.Entry.Id);
        var evidence = new List<Evidence>();
        void AddPassages(LFEntry entry, string text, int? page, string source)
        {
            // Split rather than truncate: every indexed passage is examined.
            for (var offset = 0; offset < text.Length; offset += 2500)
                evidence.Add(new Evidence(entry.Id, entry.Name, entry.FullPath, page, 1,
                    text.Substring(offset, Math.Min(2500, text.Length - offset)), source));
        }
        foreach (var document in scope.Documents)
        {
            var metadata = string.Join("\n", document.Fields.Where(field => !string.IsNullOrWhiteSpace(field.Value))
                .Select(field => $"{field.FieldName}: {field.Value}"));
            AddPassages(document.Entry, "اسم الوثيقة: " + document.Entry.Name + "\n" + metadata,
                null, "laserfiche-metadata");
        }
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            const string sql = """
                select content, metadata ->> 'entry_id', metadata ->> 'page_number',
                       metadata ->> 'text_source'
                from public.documents
                where metadata ->> 'source' = 'laserfiche-reports'
                  and metadata ->> 'record_type' = 'document-chunk'
                  and lower(metadata ->> 'repository_id') = lower(@repository)
                  and coalesce(metadata ->> 'text_source', '') <> 'laserfiche-metadata'
                order by metadata ->> 'entry_id', id
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("repository", repositoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(1) || !int.TryParse(reader.GetString(1), out var id) ||
                    !live.TryGetValue(id, out var document) || reader.IsDBNull(0)) continue;
                int? page = !reader.IsDBNull(2) && int.TryParse(reader.GetString(2), out var number) ? number : null;
                AddPassages(document.Entry, reader.GetString(0), page,
                    reader.IsDBNull(3) ? "indexed-text" : reader.GetString(3));
            }
        }
        var findings = new Dictionary<int, List<string>>();
        var sources = new List<Evidence>();
        var client = clients.CreateClient("ReportsGraph");
        foreach (var batch in evidence.Chunk(2))
        {
            var request = JsonSerializer.Serialize(new
            {
                mode = "verified_extract", question, evidence = batch.Select(item => new
                { item.EntryId, item.DocumentName, item.PageNumber, item.TextSource, item.Text })
            }, GraphJsonOptions);
            using var content = new StringContent(request, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("answer", content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<VerifiedMatches>(cancellationToken);
            if (result?.Matches is null || result.Matches.Length != batch.Length ||
                !result.Matches.Select(match => match.SourceIndex).Order().SequenceEqual(Enumerable.Range(0, batch.Length)))
                throw new InvalidOperationException("لم يؤكد النموذج فحص جميع المقاطع؛ لم تُعرض نتيجة جزئية.");
            foreach (var match in result.Matches.Where(match => match.Relevant))
            {
                var source = batch[match.SourceIndex];
                if (match.Quotes is null || match.Quotes.Length == 0 || match.Quotes.Any(quote =>
                    string.IsNullOrWhiteSpace(quote) || !source.Text.Contains(quote, StringComparison.Ordinal)))
                    throw new InvalidOperationException("أنتج النموذج معلومة غير مثبتة في المصدر؛ لم تُعرض النتيجة.");
                if (!findings.TryGetValue(source.EntryId, out var quotes))
                    findings[source.EntryId] = quotes = [];
                quotes.AddRange(match.Quotes);
                sources.Add(source);
            }
        }
        var pages = evidence.Where(item => item.TextSource != "laserfiche-metadata")
            .Select(item => item.EntryId).Distinct().Count();
        var answer = new StringBuilder($"النطاق: {ScopeLabel(entryId, folder, name)}. فحصت بيانات {scope.Documents.Count} وثيقة وجميع مقاطع نص الصفحات المفهرسة المتاحة لـ {pages} منها.\n");
        answer.AppendLine($"لا يتوفر نص صفحات مفهرس لـ {scope.Documents.Count - pages} وثيقة؛ لا يمكن تأكيد نتائج عن محتواها.\n");
        if (scope.Documents.Count != scope.DiscoveredCount)
            answer.AppendLine("التغطية غير مكتملة بسبب تعذر قراءة بعض الوثائق أثناء الحصر.");
        if (findings.Count == 0) answer.AppendLine("لم أجد في البيانات والنصوص المتاحة معلومات تدعم الإجابة.");
        foreach (var (id, quotes) in findings.OrderBy(pair => pair.Key))
        {
            answer.AppendLine($"• {live[id].Entry.Name} — ID {id}");
            foreach (var quote in quotes.Distinct()) answer.AppendLine($"  {quote}");
            answer.AppendLine();
        }
        return new ChatResult(answer.ToString().TrimEnd(), sources);
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
