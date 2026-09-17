using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Provides entry-level operations: retrieving individual entries, metadata fields,
/// templates, hierarchy paths, and folder contents. All repository data comes from
/// the active Laserfiche Repository API session.
/// </summary>
public interface ILaserficheEntryService
{
    /// <summary>Retrieves a single entry by its numeric Laserfiche Entry ID.</summary>
    Task<LFEntry> GetEntryAsync(int entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all metadata field values applied to the specified entry.
    /// Returns an empty list for entries with no field values.
    /// </summary>
    Task<IReadOnlyList<LFFieldValue>> GetEntryFieldsAsync(
        int entryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the template applied to the specified entry and its current field schema.
    /// Missing template ID/name properties are resolved against repository definitions when possible.
    /// Returns <c>null</c> only when no template is assigned or it cannot be resolved.
    /// </summary>
    Task<LFTemplate?> GetEntryTemplateAsync(
        int entryId,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves the full repository path for the specified entry.</summary>
    Task<string> GetEntryPathAsync(int entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a caller-requested page of direct folder children. The implementation first
    /// follows every Repository API continuation page so <see cref="PagedResult{T}.TotalCount"/>
    /// represents the complete direct-child set rather than only the first server page.
    /// </summary>
    Task<PagedResult<LFEntry>> GetEntryChildrenAsync(
        int entryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers the authoritative repository root via the Repository API ByPath lookup for
    /// <c>\</c>. A positive administrator-configured RootEntryId may be used only as an
    /// explicit fallback when discovery fails. No implicit assumption that entry 1 is root.
    /// </summary>
    Task<int> GetRootEntryIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves every direct child of a folder, following all server-provided
    /// <c>@odata.nextLink</c>/<c>nextLink</c> continuations and de-duplicating by Entry ID.
    /// The method fails when a page cannot be retrieved rather than returning silently
    /// incomplete data.
    /// </summary>
    Task<IReadOnlyList<LFEntry>> GetAllFolderChildrenAsync(
        int entryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a flat list of folder entries up to the requested depth below
    /// <paramref name="rootEntryId"/>, using complete child listings at every level.
    /// </summary>
    Task<IReadOnlyList<LFEntry>> GetFolderTreeAsync(
        int rootEntryId,
        int depth,
        CancellationToken cancellationToken = default);
}
