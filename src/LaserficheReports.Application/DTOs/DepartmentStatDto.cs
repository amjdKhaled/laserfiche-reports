namespace LaserficheReports.Application.DTOs;

/// <summary>
/// Represents the entry distribution for a single top-level Laserfiche folder (department).
/// Counts are approximated by sampling the most recently modified entries — full traversal
/// is deliberately avoided to keep dashboard load times acceptable.
/// </summary>
public sealed record DepartmentStatDto
{
    /// <summary>Display name of the department folder.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Laserfiche entry ID of the department folder.
    /// Zero when the record was derived from search-result path sampling rather than a direct folder listing.
    /// </summary>
    public int EntryId { get; init; }

    /// <summary>
    /// Approximate number of entries belonging to this department,
    /// counted from the recent-entry sample returned by the dashboard search.
    /// </summary>
    public int DocumentCount { get; init; }
}
