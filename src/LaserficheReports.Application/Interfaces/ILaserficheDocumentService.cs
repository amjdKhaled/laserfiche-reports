using LaserficheReports.Domain.Entities;
using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Provides document retrieval operations: page metadata, electronic document streaming,
/// and page image retrieval. All content is streamed directly from Laserfiche —
/// no temporary files are written to disk on the portal server.
/// </summary>
public interface ILaserficheDocumentService
{
    /// <summary>
    /// Retrieves metadata about each page of the specified document.
    /// Returns an empty list for entries that are not documents or have no pages.
    /// </summary>
    /// <param name="entryId">Laserfiche Entry ID of the document.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task<IReadOnlyList<LFDocumentPage>> GetDocumentPagesAsync(
        int entryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the electronic document (edoc) for the specified entry with response
    /// headers preserved. The returned object owns the upstream response and stream;
    /// callers must dispose it after the portal response has completed. The file is
    /// never buffered in memory.
    /// </summary>
    /// <param name="entryId">Laserfiche Entry ID of the document.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task<LaserficheEdocStream> StreamEdocAsync(
        int entryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the rendered image for a single page as a proxied stream that
    /// carries the Laserfiche-supplied content type. The returned object owns the
    /// upstream response; callers must dispose it after streaming the response.
    /// </summary>
    /// <param name="entryId">Laserfiche Entry ID of the document.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task<LaserficheEdocStream> GetPageImageAsync(
        int entryId,
        int pageNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the searchable text stored for a single Laserfiche page.
    /// Returns <c>null</c> when the page has no text or the active API version
    /// does not expose the V2 page-text endpoint.
    /// </summary>
    Task<string?> GetPageTextAsync(
        int entryId,
        int pageNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the entry metadata for a document, including file size and page count.
    /// Equivalent to <see cref="ILaserficheEntryService.GetEntryAsync"/> but scoped
    /// to document-type entries for semantic clarity.
    /// </summary>
    Task<LFEntry> GetDocumentMetadataAsync(
        int entryId,
        CancellationToken cancellationToken = default);
}
