namespace LaserficheReports.Application.DTOs;

/// <summary>
/// A frequently-searched query term along with its search count,
/// sourced from the portal's in-memory search audit log.
/// Used to populate the "Top Searched Queries" panel on the dashboard.
/// </summary>
public sealed record TopQueryDto
{
    /// <summary>The search query string as submitted by the user.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Number of times this exact query was submitted through the portal.</summary>
    public int Count { get; init; }
}
