using LaserficheReports.Domain.Common;

namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Represents the contents of a Laserfiche folder, including the folder's own identity
/// and a paged list of its direct children.
/// </summary>
public sealed record LFFolderListing
{
    /// <summary>Entry ID of the folder whose children are listed.</summary>
    public int FolderId { get; init; }

    /// <summary>Display name of the folder.</summary>
    public string FolderName { get; init; } = string.Empty;

    /// <summary>Full repository path of the folder.</summary>
    public string FolderPath { get; init; } = string.Empty;

    /// <summary>Paged collection of direct children (documents, subfolders, shortcuts).</summary>
    public PagedResult<LFEntry> Children { get; init; } = PagedResult<LFEntry>.Empty;
}
