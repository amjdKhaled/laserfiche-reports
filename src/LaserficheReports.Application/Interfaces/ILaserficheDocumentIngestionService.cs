using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>Indexes Laserfiche documents in the local reporting database.</summary>
public interface ILaserficheDocumentIngestionService
{
    Task<DocumentIngestionResult> ReindexContentAsync(int entryId, CancellationToken cancellationToken = default, bool rebuild = false) => IngestMetadataAsync(entryId,cancellationToken);
    Task<DocumentIngestionResult> RefreshMetadataAsync(int entryId, CancellationToken cancellationToken = default) => IngestMetadataAsync(entryId,cancellationToken);
    Task DeleteAsync(int entryId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Delete is not supported by this adapter.");

    /// <summary>
    /// Reads one document, its metadata, and page text; uses local OCR when needed;
    /// then creates or refreshes its document and embedded chunk rows in the existing
    /// <c>public.documents</c> table.
    /// </summary>
    Task<DocumentIngestionResult> IngestMetadataAsync(
        int entryId,
        CancellationToken cancellationToken = default);
}
