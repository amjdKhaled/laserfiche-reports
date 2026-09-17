namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Describes a single page within a Laserfiche document.
/// </summary>
public sealed record LFDocumentPage
{
    /// <summary>1-based page number within the document.</summary>
    public int PageNumber { get; init; }

    /// <summary>Width of the page image in pixels. Null if unavailable.</summary>
    public int? Width { get; init; }

    /// <summary>Height of the page image in pixels. Null if unavailable.</summary>
    public int? Height { get; init; }

    /// <summary>MIME type of the page image, e.g. <c>image/png</c>.</summary>
    public string? MimeType { get; init; }
}
