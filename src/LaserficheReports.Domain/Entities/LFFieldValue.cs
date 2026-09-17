namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Represents the value of a single metadata field on a Laserfiche entry.
/// </summary>
public sealed record LFFieldValue
{
    /// <summary>
    /// Numeric ID of the field definition in the Laserfiche repository.
    /// Used to join with repository-wide FieldDefinitions when the entry
    /// fields response does not include a human-readable name.
    /// </summary>
    public int FieldDefinitionId { get; init; }

    /// <summary>Name of the metadata field as defined in the Laserfiche template.</summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>
    /// Current value stored in this field. May be null if the field has no value.
    /// Multi-value fields are represented as a single joined string.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Laserfiche field data type, e.g. <c>String</c>, <c>Integer</c>, <c>DateTime</c>,
    /// <c>Number</c>, <c>List</c>, <c>LongInteger</c>.
    /// </summary>
    public string? FieldType { get; init; }

    /// <summary><c>true</c> if the template definition marks this field as required.</summary>
    public bool IsRequired { get; init; }

    /// <summary><c>true</c> if this field allows multiple values.</summary>
    public bool IsMultiValue { get; init; }
}
