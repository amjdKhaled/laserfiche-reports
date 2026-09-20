using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>Indexes Laserfiche documents in the local reporting database.</summary>
public interface ILaserficheDocumentIngestionService
{
    /// <summary>
    /// Reads one document, its metadata, and page text; uses local OCR when needed;
    /// then creates or refreshes its document and embedded chunk rows in the existing
    /// <c>public.documents</c> table.
    /// </summary>
    Task<DocumentIngestionResult> IngestMetadataAsync(
        int entryId,
        CancellationToken cancellationToken = default);
}
