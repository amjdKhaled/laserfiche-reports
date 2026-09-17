using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Retrieves template definitions from the active Laserfiche Repository API.
/// </summary>
public interface ILaserficheTemplateService
{
    /// <summary>
    /// Returns every template definition from the active repository, following all
    /// server-provided continuation pages. Returns an empty list only when the repository
    /// successfully reports no templates; source/API failures are surfaced to the caller.
    /// </summary>
    Task<IReadOnlyList<LFTemplateDefinition>> GetTemplateDefinitionsAsync(
        CancellationToken cancellationToken = default);
}
