using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Determines which Laserfiche repository is currently active and provides access
/// to all configured repositories.
/// </summary>
/// <remarks>
/// <para>
/// All service methods that need a repository identity call
/// <see cref="GetActiveRepositoryAsync"/> rather than reading configuration directly.
/// This keeps repository-selection logic in one place and makes the application
/// ready for multi-repository support without any service or controller changes.
/// </para>
/// <para>
/// Extension point: the default <c>ConfigurationRepositoryContext</c> reads a single
/// repository from <c>appsettings.json</c>. A future <c>MultiRepositoryContext</c>
/// implementation can support multiple repositories and per-session active-repository
/// selection by reading from session state — registered in DI with zero changes to
/// callers.
/// </para>
/// </remarks>
public interface IRepositoryContext
{
    /// <summary>
    /// Returns the currently active <see cref="RepositoryDescriptor"/>.
    /// For single-repository deployments this always returns the configured repository.
    /// </summary>
    Task<RepositoryDescriptor> GetActiveRepositoryAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all configured repositories. In single-repository deployments this
    /// returns a list containing exactly one item.
    /// </summary>
    Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(
        CancellationToken cancellationToken = default);
}
