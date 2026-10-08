using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Application.Interfaces;

public interface ILaserficheTagDefinitionService
{
    Task<IReadOnlyList<LFTagDefinition>> GetTagDefinitionsAsync(CancellationToken cancellationToken = default);
}
