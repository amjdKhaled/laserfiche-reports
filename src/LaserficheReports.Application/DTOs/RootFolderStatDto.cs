namespace LaserficheReports.Application.DTOs;

/// <summary>
/// Document and sub-folder counts for a single root-level (top-level) Laserfiche folder.
/// Populated by the recursive folder scan performed during every analytics refresh.
/// Used as data for the "Documents by Folder" bar chart.
/// </summary>
public sealed record RootFolderStatDto
{
    /// <summary>Entry ID of the root-level folder.</summary>
    public int EntryId { get; init; }

    /// <summary>Display name of the root folder.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Total number of documents (at any depth) beneath this folder.</summary>
    public int Documents { get; init; }

    /// <summary>Total number of sub-folders (at any depth) beneath this folder.</summary>
    public int Folders { get; init; }

    /// <summary>
    /// Entry IDs of the documents counted beneath this root folder. Keeping the
    /// authoritative IDs lets Archive drill-downs reproduce the chart exactly.
    /// </summary>
    public IReadOnlyList<int> DocumentIds { get; init; } = [];
}
