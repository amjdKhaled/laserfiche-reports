namespace LaserficheReports.Domain.Common;

/// <summary>
/// A generic, immutable paged result set returned by any list or search operation.
/// </summary>
/// <typeparam name="T">Type of item in the result set.</typeparam>
public sealed record PagedResult<T>
{
    /// <summary>Items on the current page.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>Total number of items across all pages.</summary>
    public int TotalCount { get; init; }

    /// <summary>Current 1-based page number.</summary>
    public int PageNumber { get; init; } = 1;

    /// <summary>Maximum number of items per page.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary><c>true</c> when more pages are available after the current one.</summary>
    public bool HasNextPage => PageNumber * PageSize < TotalCount;

    /// <summary><c>true</c> when the current page is not the first.</summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary>Total number of pages, derived from <see cref="TotalCount"/> and <see cref="PageSize"/>.</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

    /// <summary>Returns an empty result set with no items and a total count of zero.</summary>
    public static PagedResult<T> Empty => new();
}
