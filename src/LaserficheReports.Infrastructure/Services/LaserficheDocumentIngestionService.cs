using System.Text.Json;
using System.Globalization;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
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
    private readonly IOcrTextCorrectionService _ocrCorrection;
    private readonly ITextEmbeddingService _embeddings;
    private readonly IRepositoryContext _repositoryContext;
    private readonly SupabaseOptions _options;
    private readonly LocalAiOptions _localAiOptions;
    private readonly PaddleOcrOptions _ocrOptions;
    private readonly ILogger<LaserficheDocumentIngestionService> _logger;

    public LaserficheDocumentIngestionService(
        ILaserficheEntryService entries,
        ILaserficheDocumentService documents,
        ILocalOcrService ocr,
        IOcrTextCorrectionService ocrCorrection,
        ITextEmbeddingService embeddings,
        IRepositoryContext repositoryContext,
        IOptions<SupabaseOptions> options,
        IOptions<LocalAiOptions> localAiOptions,
        IOptions<PaddleOcrOptions> ocrOptions,
        ILogger<LaserficheDocumentIngestionService> logger)
    {
        _entries = entries;
        _documents = documents;
        _ocr = ocr;
        _ocrCorrection = ocrCorrection;
        _embeddings = embeddings;
        _repositoryContext = repositoryContext;
        _options = options.Value;
        _localAiOptions = localAiOptions.Value;
        _ocrOptions = ocrOptions.Value;
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

        IReadOnlyList<LFDocumentPage> discoveredPages = [];
        if (entry.PageCount is not > 0)
        {
            try
            {
                discoveredPages = await _documents
                    .GetDocumentPagesAsync(entryId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LaserficheReports.Domain.Exceptions.LaserficheException exception)
                when (exception.StatusCode == (int)System.Net.HttpStatusCode.NotFound)
            {
                // V2 does not expose a GET page collection on every installation.
                // Electronic documents can also report pageCount=0 while the Export
                // endpoint can still render their first page as PNG.
                _logger.LogInformation(
                    "Laserfiche did not expose a page collection for Entry {EntryId}; " +
                    "the ingestion fallback will probe page 1 through Export.",
                    entryId);
            }
        }

        var resolvedPageNumbers = ResolvePageNumbers(entry.PageCount, discoveredPages);
        var isFallbackPageProbe = entry.PageCount is not > 0 && discoveredPages.Count == 0;
        var candidatePageNumbers = isFallbackPageProbe
            ? Enumerable.Range(1, _ocrOptions.EffectiveMaxFallbackPages).ToArray()
            : resolvedPageNumbers;

        _logger.LogInformation(
            "Content ingestion page discovery. EntryId={EntryId}; ReportedPageCount={ReportedPageCount}; " +
            "DiscoveredPageCount={DiscoveredPageCount}; CandidatePages={CandidatePages}.",
            entryId,
            entry.PageCount ?? 0,
            discoveredPages.Count,
            isFallbackPageProbe ? "sequential-export-probe" : string.Join(",", candidatePageNumbers));

        var pageTexts = new List<IndexedPageText>();
        var detectedPageNumbers = new HashSet<int>();
        var ocrAttemptCount = 0;
        var ocrCorrectionAttemptCount = 0;
        var ocrCorrectedPageCount = 0;
        string? ocrCorrectionModel = null;
        var contentFailureCount = 0;
        LocalOcrException? ocrFailure = null;
        foreach (var pageNumber in candidatePageNumbers)
        {
            string? pageText = null;
            try
            {
                pageText = await _documents
                    .GetPageTextAsync(entryId, pageNumber, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Laserfiche page text retrieval failed for Entry {EntryId}, page {PageNumber}; trying local OCR.",
                    entryId,
                    pageNumber);
            }

            if (!string.IsNullOrWhiteSpace(pageText))
            {
                detectedPageNumbers.Add(pageNumber);
                pageTexts.Add(new IndexedPageText(pageNumber, pageText, "laserfiche"));
                continue;
            }

            try
            {
                using var pageImage = await _documents
                    .GetPageImageAsync(entryId, pageNumber, cancellationToken)
                    .ConfigureAwait(false);
                detectedPageNumbers.Add(pageNumber);
                ocrAttemptCount++;
                var ocrText = await _ocr
                    .TryExtractTextAsync(pageImage.Content, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    var correction = await _ocrCorrection
                        .CorrectAsync(ocrText, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(correction.Model))
                    {
                        ocrCorrectionAttemptCount++;
                        ocrCorrectionModel = correction.Model;
                    }
                    if (correction.WasCorrected)
                    {
                        ocrCorrectedPageCount++;
                    }
                    pageTexts.Add(new IndexedPageText(pageNumber, correction.Text, "ocr"));
                }
                else
                {
                    _logger.LogWarning(
                        "PaddleOCR returned no usable text for Laserfiche Entry {EntryId}, page {PageNumber}.",
                        entryId,
                        pageNumber);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LocalOcrException exception)
            {
                contentFailureCount++;
                ocrFailure ??= exception;
                _logger.LogError(
                    exception,
                    "PaddleOCR infrastructure failure for Laserfiche Entry {EntryId}, page {PageNumber}.",
                    entryId,
                    pageNumber);
                break;
            }
            catch (LaserficheReports.Domain.Exceptions.LaserficheException exception)
                when (isFallbackPageProbe && pageNumber > 1 &&
                      exception.StatusCode is >= 400 and < 500)
            {
                _logger.LogInformation(
                    "Sequential page export completed for Entry {EntryId}; page {PageNumber} was not available (HTTP {StatusCode}).",
                    entryId,
                    pageNumber,
                    exception.StatusCode);
                break;
            }
            catch (Exception exception)
            {
                contentFailureCount++;
                _logger.LogWarning(
                    exception,
                    "Local OCR fallback failed for Laserfiche Entry {EntryId}, page {PageNumber}. " +
                    "The document will remain available with metadata only for this page.",
                    entryId,
                    pageNumber);

                if (isFallbackPageProbe)
                    break;
            }
        }

        var laserficheTextPageCount = pageTexts.Count(page => page.Source == "laserfiche");
        var ocrTextPageCount = pageTexts.Count(page => page.Source == "ocr");
        var hasUsableText = pageTexts.Count > 0;
        if (!hasUsableText && ocrFailure is not null)
        {
            // Do not overwrite an existing indexed document or delete its chunks
            // merely because the local OCR worker is temporarily unavailable.
            throw ocrFailure;
        }

        var ingestionStatus = hasUsableText ? "content-indexed" : "metadata-only";
        var textSource = ResolveTextSource(laserficheTextPageCount, ocrTextPageCount);
        var contentDiagnostic = ResolveContentDiagnostic(
            hasUsableText,
            detectedPageNumbers.Count,
            ocrAttemptCount,
            ocrTextPageCount,
            contentFailureCount);
        var chunks = hasUsableText
            ? PageTextChunker.Split(
                pageTexts,
                _localAiOptions.EffectiveChunkSize,
                _localAiOptions.EffectiveChunkOverlap)
            : Array.Empty<PageTextChunker.TextChunk>();
        var embeddings = chunks.Count > 0
            ? await _embeddings.CreateEmbeddingsAsync(
                chunks.Select(chunk => _localAiOptions.DocumentEmbeddingPrefix + chunk.Content).ToArray(),
                cancellationToken).ConfigureAwait(false)
            : Array.Empty<float[]>();
        var metadata = BuildMetadata(
            repository.RepositoryId,
            entry,
            fields,
            ingestionStatus,
            textSource,
            pageTexts.Count,
            laserficheTextPageCount,
            ocrTextPageCount,
            pageTexts,
            chunks.Count,
            chunks.Count > 0 ? _localAiOptions.EmbeddingModel : null,
            detectedPageNumbers.Count,
            ocrAttemptCount,
            ocrCorrectionAttemptCount,
            ocrCorrectedPageCount,
            ocrCorrectionModel,
            contentDiagnostic);
        var content = BuildIndexedContent(entry, fields, pageTexts);

        await using var connection = new NpgsqlConnection(_options.PostgresConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var (documentRowId, wasInserted) = await UpsertDocumentAsync(
            connection,
            transaction,
            content,
            metadata,
            repository.RepositoryId,
            entryId,
            cancellationToken).ConfigureAwait(false);

        await DeleteExistingChunksAsync(
            connection,
            transaction,
            repository.RepositoryId,
            entryId,
            cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var chunkMetadata = BuildChunkMetadata(
                repository.RepositoryId,
                entry,
                documentRowId,
                chunk,
                chunks.Count,
                _localAiOptions.EmbeddingModel,
                _localAiOptions.EffectiveEmbeddingDimensions);

            await InsertChunkAsync(
                connection,
                transaction,
                chunk.Content,
                chunkMetadata,
                embeddings[index],
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "{Action} Laserfiche row {RowId} and stored {ChunkCount} embedded chunks for repository {RepositoryId}, Entry {EntryId}.",
            wasInserted ? "Inserted" : "Updated",
            documentRowId,
            chunks.Count,
            repository.RepositoryId,
            entryId);

        return new DocumentIngestionResult(
            documentRowId,
            entryId,
            repository.RepositoryId,
            entry.Name,
            fields.Count,
            wasInserted,
            ingestionStatus,
            chunks.Count,
            chunks.Count > 0 ? _localAiOptions.EmbeddingModel : null,
            detectedPageNumbers.Count,
            ocrAttemptCount,
            ocrTextPageCount,
            ocrCorrectionAttemptCount,
            ocrCorrectedPageCount,
            ocrCorrectionModel,
            contentDiagnostic);
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
        IReadOnlyList<IndexedPageText>? textPages = null,
        int chunkCount = 0,
        string? embeddingModel = null,
        int detectedPageCount = 0,
        int ocrAttemptCount = 0,
        int ocrCorrectionAttemptCount = 0,
        int ocrCorrectedPageCount = 0,
        string? ocrCorrectionModel = null,
        string? contentDiagnostic = null)
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
            detected_page_count = detectedPageCount,
            file_size_bytes = entry.FileSizeBytes,
            creator = entry.Creator,
            creation_time = entry.CreationTime,
            last_modified_time = entry.LastModifiedTime,
            ingestion_status = ingestionStatus,
            text_source = textSource,
            text_page_count = textPageCount,
            laserfiche_text_page_count = laserficheTextPageCount,
            ocr_text_page_count = ocrTextPageCount,
            ocr_attempt_count = ocrAttemptCount,
            ocr_correction_attempt_count = ocrCorrectionAttemptCount,
            ocr_corrected_page_count = ocrCorrectedPageCount,
            ocr_correction_model = ocrCorrectionModel,
            content_diagnostic = contentDiagnostic,
            chunk_count = chunkCount,
            embedding_model = embeddingModel,
            embedding_status = chunkCount > 0 ? "complete" : "not-created",
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

    internal static IReadOnlyList<int> ResolvePageNumbers(
        int? reportedPageCount,
        IReadOnlyList<LFDocumentPage> discoveredPages)
    {
        if (reportedPageCount is > 0)
            return Enumerable.Range(1, reportedPageCount.Value).ToArray();

        var pageNumbers = discoveredPages
            .Select(page => page.PageNumber)
            .Where(pageNumber => pageNumber > 0)
            .Distinct()
            .OrderBy(pageNumber => pageNumber)
            .ToArray();

        // An electronic document can legitimately report zero repository image
        // pages while V2 Export can render page 1. Always probe it once so OCR is
        // actually invoked instead of silently producing metadata-only rows.
        return pageNumbers.Length > 0 ? pageNumbers : [1];
    }

    internal static string? ResolveContentDiagnostic(
        bool hasUsableText,
        int detectedPageCount,
        int ocrAttemptCount,
        int ocrTextPageCount,
        int contentFailureCount)
    {
        if (hasUsableText && contentFailureCount == 0) return null;
        if (hasUsableText)
            return $"Content was indexed partially; {contentFailureCount} page operation(s) failed.";
        if (detectedPageCount == 0)
            return "No document pages were detected.";
        if (contentFailureCount > 0)
            return $"OCR was attempted {ocrAttemptCount} time(s), but {contentFailureCount} page operation(s) failed. Check the application and PaddleOCR worker logs.";
        if (ocrAttemptCount > 0 && ocrTextPageCount == 0)
            return $"PaddleOCR was called for {ocrAttemptCount} page(s) but returned no usable text.";
        return "No searchable document text was available.";
    }

    internal sealed record IndexedPageText(int PageNumber, string Text, string Source);

    internal static string BuildChunkMetadata(
        string repositoryId,
        LFEntry entry,
        long parentDocumentId,
        PageTextChunker.TextChunk chunk,
        int chunkCount,
        string embeddingModel,
        int embeddingDimensions)
    {
        return JsonSerializer.Serialize(new
        {
            source = Source,
            record_type = "document-chunk",
            repository_id = repositoryId,
            entry_id = entry.Id,
            document_name = entry.Name,
            full_path = entry.FullPath,
            folder_path = entry.FolderPath,
            template_id = entry.TemplateId,
            template_name = entry.TemplateName,
            parent_document_id = parentDocumentId,
            chunk_index = chunk.Index,
            chunk_count = chunkCount,
            page_number = chunk.PageNumber,
            start_offset = chunk.StartOffset,
            end_offset = chunk.EndOffset,
            text_source = chunk.Source,
            embedding_model = embeddingModel,
            embedding_dimensions = embeddingDimensions,
            indexed_at = DateTimeOffset.UtcNow
        });
    }

    internal static string BuildVectorLiteral(IReadOnlyList<float> embedding) =>
        "[" + string.Join(",", embedding.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static async Task<(long DocumentRowId, bool WasInserted)> UpsertDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string content,
        string metadata,
        string repositoryId,
        int entryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(UpsertDocumentSql, connection, transaction);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("metadata", metadata);
        command.Parameters.AddWithValue("source", Source);
        command.Parameters.AddWithValue("recordType", RecordType);
        command.Parameters.AddWithValue("repositoryId", repositoryId);
        command.Parameters.AddWithValue("entryId", entryId.ToString(CultureInfo.InvariantCulture));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Supabase did not return the indexed document row.");

        return (reader.GetInt64(0), reader.GetBoolean(1));
    }

    private static async Task DeleteExistingChunksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string repositoryId,
        int entryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(DeleteChunksSql, connection, transaction);
        command.Parameters.AddWithValue("source", Source);
        command.Parameters.AddWithValue("repositoryId", repositoryId);
        command.Parameters.AddWithValue("entryId", entryId.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string content,
        string metadata,
        IReadOnlyList<float> embedding,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(InsertChunkSql, connection, transaction);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("metadata", metadata);
        command.Parameters.AddWithValue("embedding", BuildVectorLiteral(embedding));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string UpsertDocumentSql = """
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

    private const string DeleteChunksSql = """
        DELETE FROM public.documents
        WHERE metadata ->> 'source' = @source
          AND metadata ->> 'record_type' = 'document-chunk'
          AND metadata ->> 'repository_id' = @repositoryId
          AND metadata ->> 'entry_id' = @entryId;
        """;

    private const string InsertChunkSql = """
        INSERT INTO public.documents (content, metadata, embedding)
        VALUES (@content, CAST(@metadata AS jsonb), CAST(@embedding AS extensions.vector));
        """;
}
