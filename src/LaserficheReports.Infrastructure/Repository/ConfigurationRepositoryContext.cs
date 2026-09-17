using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Repository;

/// <summary>
/// Resolves the active repository from the live <see cref="LaserficheOptions"/>.
/// Uses <see cref="IOptionsMonitor{T}"/> so changes saved via the Settings page
/// are reflected immediately without restarting the application.
/// </summary>
/// <remarks>
/// Supports single-repository deployments only. To support multiple repositories,
/// implement <see cref="IRepositoryContext"/> with a session-backed store and register
/// it in place of this class — no service or controller code changes required.
/// </remarks>
internal sealed class ConfigurationRepositoryContext : IRepositoryContext
{
    private readonly IOptionsMonitor<LaserficheOptions> _optionsMonitor;

    /// <summary>Initialises the context with a live options monitor.</summary>
    public ConfigurationRepositoryContext(IOptionsMonitor<LaserficheOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    /// <inheritdoc />
    public Task<RepositoryDescriptor> GetActiveRepositoryAsync(
        CancellationToken cancellationToken = default)
    {
        var opt = _optionsMonitor.CurrentValue;
        var descriptor = new RepositoryDescriptor(
            Key: "default",
            ServerUrl: opt.ServerUrl.TrimEnd('/'),
            RepositoryId: opt.RepositoryId,
            DisplayName: opt.EffectiveDisplayName);
        return Task.FromResult(descriptor);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(
        CancellationToken cancellationToken = default) =>
        [await GetActiveRepositoryAsync(cancellationToken)];
}
