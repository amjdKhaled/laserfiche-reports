namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Represents a metadata template definition retrieved from the Laserfiche
/// <c>GET /TemplateDefinitions</c> endpoint. Templates define the field schemas
/// that can be applied to entries in the repository.
/// </summary>
public sealed record LFTemplateDefinition
{
    /// <summary>Unique numeric identifier of the template.</summary>
    public int Id { get; init; }

    /// <summary>Display name of the template as it appears in the Laserfiche client.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional description of the template's purpose.</summary>
    public string? Description { get; init; }
}
