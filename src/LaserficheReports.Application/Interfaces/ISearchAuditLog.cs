using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Records and queries portal-level search activity.
/// Backed by an in-memory rolling log (no external database required).
/// The log is populated whenever a user submits a search through LaserficheReports;
/// data does not persist across application restarts.
/// </summary>
/// <remarks>
/// <para>
/// This provides repository search activity data
/// and "Top Searched Queries" panel — the same source used by the original
/// GovSearch AI implementation. Laserfiche Repository API v1 does not expose
/// search history, so the portal maintains its own.
/// </para>
/// <para>
/// All operations are REPOSITORY-SCOPED: entries are recorded against the
/// repository the search ran in, and every query method returns data for a
/// single repository only. On a multi-repository server, users of one
/// repository must never see the search activity of another.
/// </para>
/// </remarks>
public interface ISearchAuditLog
{
    /// <summary>
    /// Records that a search query was submitted against the given repository.
    /// The query is stored with the current UTC timestamp.
    /// Thread-safe — may be called from multiple concurrent requests.
    /// </summary>
    /// <param name="repositoryId">The repository the search was executed in.</param>
    /// <param name="query">The raw search query string as entered by the user.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task RecordSearchAsync(string repositoryId, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the search count for each of the last <paramref name="days"/> calendar
    /// days for the given repository, ordered from oldest to newest.
    /// Days with no recorded searches are included with a count of zero.
    /// </summary>
    Task<IReadOnlyList<SearchActivityDayDto>> GetSearchesByDayAsync(
        string repositoryId,
        int days = 7,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the top <paramref name="n"/> most-frequently submitted queries
    /// for the given repository, ordered by count descending.
    /// </summary>
    Task<IReadOnlyList<TopQueryDto>> GetTopQueriesAsync(
        string repositoryId,
        int n = 5,
        CancellationToken cancellationToken = default);

    /// <summary>Total number of searches recorded for the given repository.</summary>
    Task<int> GetTotalSearchCountAsync(string repositoryId, CancellationToken cancellationToken = default);
}
