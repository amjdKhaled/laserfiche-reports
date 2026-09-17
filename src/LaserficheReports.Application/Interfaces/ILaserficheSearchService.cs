using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Provides search operations against the Laserfiche Repository API.
/// All search modes ultimately delegate to the LF API — no local indexing or caching.
/// </summary>
/// <remarks>
/// The Laserfiche search API follows a long-operation pattern: a search is submitted
/// asynchronously and the implementation polls for results. This is transparent to callers.
/// </remarks>
public interface ILaserficheSearchService
{
    /// <summary>
    /// Performs a simple keyword search using the Laserfiche SimpleSearches API.
    /// Matches entry names and full-text content where full-text indexing is enabled.
    /// </summary>
    /// <param name="query">Search keyword or phrase.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Results per page.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task<PagedResult<LFSearchResult>> SimpleSearchAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a search using a Laserfiche search expression, e.g.
    /// <c>({LF:Name}="Report*") &amp; ({LF:Template}="Finance")</c>.
    /// </summary>
    /// <param name="searchExpression">A valid Laserfiche search expression string.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Results per page.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task<PagedResult<LFSearchResult>> AdvancedSearchAsync(
        string searchExpression,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for entries that have the specified template applied.
    /// </summary>
    /// <param name="templateName">Exact template name as configured in Laserfiche.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Results per page.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task<PagedResult<LFSearchResult>> SearchByTemplateAsync(
        string templateName,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for entries where a specific metadata field contains the specified value.
    /// </summary>
    /// <param name="fieldName">Exact field name as configured in Laserfiche.</param>
    /// <param name="fieldValue">Value to match. Partial matches use <c>*</c> wildcards.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Results per page.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task<PagedResult<LFSearchResult>> SearchByFieldAsync(
        string fieldName,
        string fieldValue,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
