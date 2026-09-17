using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>Indexes Laserfiche documents in the local reporting database.</summary>
public interface ILaserficheDocumentIngestionService
{
    /// <summary>
    /// Reads one document and its metadata from Laserfiche, then creates or refreshes
    /// its metadata-only row in the existing <c>public.documents</c> table.
    /// </summary>
    Task<DocumentIngestionResult> IngestMetadataAsync(
        int entryId,
        CancellationToken cancellationToken = default);
}
