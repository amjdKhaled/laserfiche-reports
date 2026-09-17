using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Provides repository-level operations: connection testing, repository discovery,
/// and metadata retrieval. All data comes directly from the Laserfiche Repository API.
/// </summary>
public interface ILaserficheRepositoryService
{
    /// <summary>
    /// Discovers all Laserfiche repositories accessible on the specified API server
    /// using the provided credentials. Used by the Settings page to populate the
    /// repository selection dropdown without saving configuration first.
    /// </summary>
    /// <param name="serverUrl">Base URL of the Laserfiche API Server.</param>
    /// <param name="username">Laserfiche username for authentication.</param>
    /// <param name="password">Password for the specified username.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns>List of available repositories. Empty if none are accessible.</returns>
    Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(
        string serverUrl,
        string repositoryId,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the configured repository's metadata by reading the documented
    /// <c>GET /Repositories</c> list and validating the configured identifier.
    /// </summary>
    Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the connection to the currently configured active repository and returns
    /// a <see cref="ConnectionStatus"/> that is always safe to display in the UI —
    /// it never throws even when the connection fails.
    /// </summary>
    Task<ConnectionStatus> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests a connection using explicitly supplied credentials rather than the stored
    /// configuration. Used by the Settings page "Test Connection" button before saving.
    /// </summary>
    Task<ConnectionStatus> TestConnectionWithCredentialsAsync(
        string serverUrl,
        string repositoryId,
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
