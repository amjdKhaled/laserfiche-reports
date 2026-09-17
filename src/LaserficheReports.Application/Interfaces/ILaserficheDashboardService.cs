using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Aggregates live Laserfiche repository data into a single dashboard statistics object.
/// This is the only service the Dashboard controller calls — it encapsulates all
/// aggregation logic and never exposes raw API responses to the Web layer.
/// </summary>
/// <remarks>
/// <para>
/// This service calls <see cref="ILaserficheRepositoryService"/> and
/// <see cref="ILaserficheEntryService"/> internally. It never makes direct HTTP calls.
/// </para>
/// <para>
/// If any underlying Laserfiche call fails, the service returns a
/// <see cref="DashboardStatsDto"/> with <c>IsConnected = false</c> and a descriptive
/// <c>ErrorMessage</c> rather than propagating the exception. This ensures the
/// Dashboard page always renders — showing error cards rather than a crash page.
/// </para>
/// </remarks>
public interface ILaserficheDashboardService
{
    /// <summary>
    /// Fetches and aggregates live repository statistics for the Dashboard page.
    /// Never throws; errors are reported via <see cref="DashboardStatsDto.ErrorMessage"/>.
    /// </summary>
    Task<DashboardStatsDto> GetDashboardStatsAsync(CancellationToken cancellationToken = default);
}
