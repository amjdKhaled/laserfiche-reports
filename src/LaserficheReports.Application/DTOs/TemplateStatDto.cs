namespace LaserficheReports.Application.DTOs;

/// <summary>
/// Document count for a single Laserfiche metadata template.
/// Derived by collecting the <c>templateName</c> field from every entry
/// discovered during the recursive folder scan.
/// </summary>
public sealed record TemplateStatDto
{
    /// <summary>Template name as returned by the Laserfiche API.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Number of documents in the repository that have this template applied.</summary>
    public int Count { get; init; }
}
