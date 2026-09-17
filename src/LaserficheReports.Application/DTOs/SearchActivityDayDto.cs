namespace LaserficheReports.Application.DTOs;

/// <summary>
/// Number of searches recorded by the portal's in-memory audit log on a
/// specific calendar date. Provides the data points for the 7-day Search
/// Daily repository search activity used by reporting and administration views.
/// </summary>
public sealed record SearchActivityDayDto
{
    /// <summary>
    /// Calendar date in <c>yyyy-MM-dd</c> format, e.g. <c>2026-07-25</c>.
    /// </summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>
    /// Short display label for chart X-axis, e.g. <c>Jul 25</c>.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Total number of searches recorded on <see cref="Date"/>.</summary>
    public int Count { get; init; }
}
