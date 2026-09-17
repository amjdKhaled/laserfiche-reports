namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Describes the schema of a single metadata field as defined in a Laserfiche template.
/// </summary>
public sealed record LFFieldDefinition
{
    /// <summary>Numeric identifier of the field definition in Laserfiche.</summary>
    public int Id { get; init; }

    /// <summary>Display name of the field.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Laserfiche data type of the field, e.g. <c>String</c>, <c>Integer</c>,
    /// <c>DateTime</c>, <c>Number</c>, <c>List</c>.
    /// </summary>
    public string FieldType { get; init; } = string.Empty;

    /// <summary><c>true</c> if documents using this template must provide a value for this field.</summary>
    public bool IsRequired { get; init; }

    /// <summary><c>true</c> if this field accepts multiple values.</summary>
    public bool IsMultiValue { get; init; }

    /// <summary>Optional description configured in the Laserfiche administration console.</summary>
    public string? Description { get; init; }

    /// <summary>Maximum allowed length for string fields. Null for non-string types.</summary>
    public int? MaxLength { get; init; }
}
