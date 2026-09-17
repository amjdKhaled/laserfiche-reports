namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Represents a single entry (document, folder, or shortcut) in a Laserfiche repository.
/// This is an immutable value object sourced directly from the Laserfiche Repository API.
/// </summary>
/// <remarks>
/// All data originates from the live Laserfiche API. This record is never constructed
/// from local state or cached repository copies.
/// </remarks>
public sealed record LFEntry
{
    /// <summary>Unique numeric identifier assigned by Laserfiche.</summary>
    public int Id { get; init; }

    /// <summary>Display name of the entry within its parent folder.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Entry ID of the parent folder. Zero for the repository root.</summary>
    public int ParentId { get; init; }

    /// <summary>Full repository path from the root, e.g. <c>\Department\Year\Document</c>.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Path of the containing folder, excluding the entry name itself.</summary>
    public string FolderPath { get; init; } = string.Empty;

    /// <summary>Username or display name of the user who created the entry.</summary>
    public string? Creator { get; init; }

    /// <summary>UTC timestamp when the entry was first created in Laserfiche.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>UTC timestamp of the most recent modification to this entry.</summary>
    public DateTimeOffset? LastModifiedTime { get; init; }

    /// <summary>Classifies this entry as a Document, Folder, Shortcut, or RecordSeries.</summary>
    public LFEntryType EntryType { get; init; }

    /// <summary>Name of the metadata template applied to this entry, if any.</summary>
    public string? TemplateName { get; init; }

    /// <summary>Numeric ID of the applied template, if any.</summary>
    public int? TemplateId { get; init; }

    /// <summary>File size in bytes for document entries. Null for folders and shortcuts.</summary>
    public long? FileSizeBytes { get; init; }

    /// <summary>Number of pages for document entries. Null for folders.</summary>
    public int? PageCount { get; init; }

    /// <summary>Extension point: raw row number from paginated API response, used for stable sorting.</summary>
    public int? RowNumber { get; init; }
}
