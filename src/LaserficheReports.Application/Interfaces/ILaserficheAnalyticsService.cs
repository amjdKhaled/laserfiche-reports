using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Aggregates live Laserfiche repository data into a single repository statistics object.
/// This is the only service the Reports application controller calls — it encapsulates all
/// aggregation logic and never exposes raw API responses to the Web layer.
/// </summary>
/// <remarks>
/// <para>
/// This service calls <see cref="ILaserficheRepositoryService"/> and
/// <see cref="ILaserficheEntryService"/> internally. It never makes direct HTTP calls.
/// </para>
/// <para>
/// If any underlying Laserfiche call fails, the service returns a
/// <see cref="RepositoryStatsDto"/> with <c>IsConnected = false</c> and a descriptive
/// <c>ErrorMessage</c> rather than propagating the exception. This ensures the
/// Reports page always renders — showing error cards rather than a crash page.
/// </para>
/// </remarks>
public interface ILaserficheAnalyticsService
{
    /// <summary>
    /// Fetches and aggregates live repository statistics for the Reports application page.
    /// Never throws; errors are reported via <see cref="RepositoryStatsDto.ErrorMessage"/>.
    /// </summary>
    Task<RepositoryStatsDto> GetRepositoryStatsAsync(CancellationToken cancellationToken = default);
}
