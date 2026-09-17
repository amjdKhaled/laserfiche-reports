using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Retrieves repository-wide field definitions from the active Laserfiche Repository API.
/// Field definitions describe field schema; per-document values come from
/// <see cref="ILaserficheEntryService.GetEntryFieldsAsync"/>.
/// </summary>
public interface ILaserficheFieldDefinitionService
{
    /// <summary>
    /// Returns every field definition available in the active repository, following all
    /// server-provided continuation pages. Results are keyed by authoritative field ID.
    /// An empty dictionary means the repository successfully reported no field definitions;
    /// source/API failures are surfaced rather than represented as empty data.
    /// </summary>
    Task<IReadOnlyDictionary<int, LFFieldDefinition>> GetFieldDefinitionsAsync(
        CancellationToken cancellationToken = default);
}
