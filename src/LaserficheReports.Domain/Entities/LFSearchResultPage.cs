using LaserficheReports.Domain.Common;

namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Encapsulates a paged set of search results returned by the Laserfiche search API.
/// </summary>
public sealed record LFSearchResultPage
{
    /// <summary>The original query string or search expression that produced these results.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Paged collection of matching entries.</summary>
    public PagedResult<LFSearchResult> Results { get; init; } = PagedResult<LFSearchResult>.Empty;
}
