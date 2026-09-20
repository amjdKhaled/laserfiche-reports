using System.Text.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LaserficheReports.Infrastructure.Services;

internal sealed class LaserficheDocumentIngestionService : ILaserficheDocumentIngestionService
{
    private const string Source = "laserfiche-reports";
    private const string RecordType = "document-metadata";

    private readonly ILaserficheEntryService _entries;
    private readonly ILaserficheDocumentService _documents;
    private readonly ILocalOcrService _ocr;
    private readonly IRepositoryContext _repositoryContext;
    private readonly SupabaseOptions _options;
    private readonly ILogger<LaserficheDocumentIngestionService> _logger;

    public LaserficheDocumentIngestionService(
        ILaserficheEntryService entries,
        ILaserficheDocumentService documents,
        ILocalOcrService ocr,
        IRepositoryContext repositoryContext,
        IOptions<SupabaseOptions> options,
        ILogger<LaserficheDocumentIngestionService> logger)
    {
        _entries = entries;
        _documents = documents;
        _ocr = ocr;
        _repositoryContext = repositoryContext;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DocumentIngestionResult> IngestMetadataAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        if (entryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entryId), "Laserfiche Entry ID must be positive.");
        }

        if (string.IsNullOrWhiteSpace(_options.PostgresConnectionString))
        {
            throw new InvalidOperationException(
                "Supabase:PostgresConnectionString is missing. Configure it in appsettings.Local.json.");
        }

        var repository = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);
        var entry = await _entries.GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false);

        if (entry.EntryType != LFEntryType.Document)
        {
            throw new InvalidOperationException($"Laserfiche Entry {entryId} is not a document.");
        }

        var fields = await _entries
            .GetEntryFieldsAsync(entryId, cancellationToken)
            .ConfigureAwait(false);

        var pageNumbers = entry.PageCount is > 0
            ? Enumerable.Range(1, entry.PageCount.Value).ToArray()
            : (await _documents.GetDocumentPagesAsync(entryId, cancellationToken).ConfigureAwait(false))
                .Select(page => page.PageNumber)
                .Distinct()
                .OrderBy(pageNumber => pageNumber)
                .ToArray();

        var pageTexts = new List<IndexedPageText>();
        foreach (var pageNumber in pageNumbers)
        {
            var pageText = await _documents
                .GetPageTextAsync(entryId, pageNumber, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(pageText))
            {
                pageTexts.Add(new IndexedPageText(pageNumber, pageText, "laserfiche"));
                continue;
            }

            try
            {
                using var pageImage = await _documents
                    .GetPageImageAsync(entryId, pageNumber, cancellationToken)
                    .ConfigureAwait(false);
                var ocrText = await _ocr
                    .TryExtractTextAsync(pageImage.Content, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(ocrText))
                    pageTexts.Add(new IndexedPageText(pageNumber, ocrText, "ocr"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Local OCR fallback failed for Laserfiche Entry {EntryId}, page {PageNumber}. " +
                    "The document will remain available with metadata only for this page.",
                    entryId,
                    pageNumber);
            }
        }

        var laserficheTextPageCount = pageTexts.Count(page => page.Source == "laserfiche");
        var ocrTextPageCount = pageTexts.Count(page => page.Source == "ocr");
        var hasUsableText = pageTexts.Count > 0;
        var ingestionStatus = hasUsableText ? "content-indexed" : "metadata-only";
        var textSource = ResolveTextSource(laserficheTextPageCount, ocrTextPageCount);
        var metadata = BuildMetadata(
            repository.RepositoryId,
            entry,
            fields,
            ingestionStatus,
            textSource,
            pageTexts.Count,
            laserficheTextPageCount,
            ocrTextPageCount,
            pageTexts);
        var content = BuildIndexedContent(entry, fields, pageTexts);

        await using var connection = new NpgsqlConnection(_options.PostgresConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("metadata", metadata);
        command.Parameters.AddWithValue("source", Source);
        command.Parameters.AddWithValue("recordType", RecordType);
        command.Parameters.AddWithValue("repositoryId", repository.RepositoryId);
        command.Parameters.AddWithValue("entryId", entryId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Supabase did not return the indexed document row.");
        }

        var documentRowId = reader.GetInt64(0);
        var wasInserted = reader.GetBoolean(1);

        _logger.LogInformation(
            "{Action} Laserfiche metadata row {RowId} for repository {RepositoryId}, Entry {EntryId}.",
            wasInserted ? "Inserted" : "Updated",
            documentRowId,
            repository.RepositoryId,
            entryId);

        return new DocumentIngestionResult(
            documentRowId,
            entryId,
            repository.RepositoryId,
            entry.Name,
            fields.Count,
            wasInserted,
            ingestionStatus);
    }

    internal static string BuildMetadata(
        string repositoryId,
        LFEntry entry,
        IReadOnlyList<LFFieldValue> fields,
        string ingestionStatus = "metadata-only",
        string textSource = "none",
        int textPageCount = 0,
        int laserficheTextPageCount = 0,
        int ocrTextPageCount = 0,
        IReadOnlyList<IndexedPageText>? textPages = null)
    {
        var payload = new
        {
            source = Source,
            record_type = RecordType,
            repository_id = repositoryId,
            entry_id = entry.Id,
            document_name = entry.Name,
            full_path = entry.FullPath,
            folder_path = entry.FolderPath,
            template_id = entry.TemplateId,
            template_name = entry.TemplateName,
            page_count = entry.PageCount,
            file_size_bytes = entry.FileSizeBytes,
            creator = entry.Creator,
            creation_time = entry.CreationTime,
            last_modified_time = entry.LastModifiedTime,
            ingestion_status = ingestionStatus,
            text_source = textSource,
            text_page_count = textPageCount,
            laserfiche_text_page_count = laserficheTextPageCount,
            ocr_text_page_count = ocrTextPageCount,
            text_pages = (textPages ?? Array.Empty<IndexedPageText>())
                .OrderBy(page => page.PageNumber)
                .Select(page => new
                {
                    page_number = page.PageNumber,
                    source = page.Source
                }),
            indexed_at = DateTimeOffset.UtcNow,
            fields = fields.Select(field => new
            {
                id = field.FieldDefinitionId,
                name = field.FieldName,
                value = field.Value,
                type = field.FieldType,
                is_multi_value = field.IsMultiValue
            })
        };

        return JsonSerializer.Serialize(payload);
    }

    internal static string BuildMetadataContent(
        LFEntry entry,
        IReadOnlyList<LFFieldValue> fields)
    {
        var populatedFields = fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => $"{field.FieldName}: {field.Value}");

        return string.Join(Environment.NewLine,
            new[] { $"Document: {entry.Name}", $"Path: {entry.FullPath}" }
                .Concat(populatedFields));
    }

    internal static string BuildIndexedContent(
        LFEntry entry,
        IReadOnlyList<LFFieldValue> fields,
        IReadOnlyList<IndexedPageText> pageTexts)
    {
        var sections = new List<string> { BuildMetadataContent(entry, fields) };
        sections.AddRange(pageTexts
            .Where(page => !string.IsNullOrWhiteSpace(page.Text))
            .OrderBy(page => page.PageNumber)
            .Select(page => $"Page {page.PageNumber}:{Environment.NewLine}{page.Text.Trim()}"));

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    internal static string ResolveTextSource(int laserficheTextPageCount, int ocrTextPageCount)
    {
        if (laserficheTextPageCount > 0 && ocrTextPageCount > 0) return "mixed";
        if (laserficheTextPageCount > 0) return "laserfiche";
        if (ocrTextPageCount > 0) return "ocr";
        return "none";
    }

    internal sealed record IndexedPageText(int PageNumber, string Text, string Source);

    private const string Sql = """
        WITH updated AS (
            UPDATE public.documents
            SET content = @content,
                metadata = CAST(@metadata AS jsonb),
                embedding = NULL
            WHERE metadata ->> 'source' = @source
              AND metadata ->> 'record_type' = @recordType
              AND metadata ->> 'repository_id' = @repositoryId
              AND metadata ->> 'entry_id' = @entryId
            RETURNING id
        ),
        inserted AS (
            INSERT INTO public.documents (content, metadata)
            SELECT @content, CAST(@metadata AS jsonb)
            WHERE NOT EXISTS (SELECT 1 FROM updated)
            RETURNING id
        )
        SELECT id, FALSE AS was_inserted FROM updated
        UNION ALL
        SELECT id, TRUE AS was_inserted FROM inserted
        LIMIT 1;
        """;
}
