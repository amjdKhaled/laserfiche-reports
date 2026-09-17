namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Represents a Laserfiche metadata template, including its field schema.
/// Templates define which metadata fields are associated with entries.
/// </summary>
public sealed record LFTemplate
{
    /// <summary>Numeric identifier of the template in Laserfiche.</summary>
    public int Id { get; init; }

    /// <summary>Display name of the template as configured in Laserfiche.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional description configured in the Laserfiche administration console.</summary>
    public string? Description { get; init; }

    /// <summary>Ordered list of field definitions that belong to this template.</summary>
    public IReadOnlyList<LFFieldDefinition> Fields { get; init; } = [];
}
