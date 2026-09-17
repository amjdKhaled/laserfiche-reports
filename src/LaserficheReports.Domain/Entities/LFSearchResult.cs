namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Represents a single entry returned by a Laserfiche search operation.
/// </summary>
public sealed record LFSearchResult
{
    /// <summary>Entry ID of the matching entry.</summary>
    public int EntryId { get; init; }

    /// <summary>Display name of the matching entry.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Full repository path of the matching entry.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Type of the matching entry.</summary>
    public LFEntryType EntryType { get; init; }

    /// <summary>Name of the template applied to this entry, if any.</summary>
    public string? TemplateName { get; init; }

    public int? TemplateId { get; init; }

    /// <summary>Creator of this entry.</summary>
    public string? Creator { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTimeOffset? LastModifiedTime { get; init; }
}
