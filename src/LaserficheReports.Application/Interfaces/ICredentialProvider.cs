using LaserficheReports.Domain.Common;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Provides Laserfiche credentials from a secure store without exposing them in
/// configuration files or application memory longer than necessary.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must never log, serialise, or persist credentials in plain text.
/// The concrete implementation (DPAPI, Windows Credential Manager, or environment
/// variable fallback for development) is selected by the <c>CredentialProvider</c>
/// setting in <c>appsettings.json</c> and registered in the DI container by
/// <c>AddLaserficheInfrastructure()</c>. Swapping implementations requires only a
/// one-line DI change — no Application or Web layer code changes.
/// </para>
/// <para>
/// Extension point: future implementations may source credentials from an HSM,
/// Azure Key Vault (if internet is permitted), or an enterprise secrets manager.
/// </para>
/// </remarks>
public interface ICredentialProvider
{
    /// <summary>
    /// Retrieves the credentials for the specified repository key from the secure store.
    /// </summary>
    /// <param name="repositoryKey">
    /// Unique configuration key identifying the repository, matching
    /// <see cref="DTOs.RepositoryDescriptor.Key"/>.
    /// </param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns>A <see cref="LaserficheCredential"/> containing the username and password.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no credentials are stored for <paramref name="repositoryKey"/>.
    /// </exception>
    Task<LaserficheCredential> GetCredentialsAsync(
        string repositoryKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Securely stores or updates credentials for the specified repository key.
    /// Replaces any previously stored credentials for the same key.
    /// </summary>
    /// <param name="repositoryKey">Unique configuration key identifying the repository.</param>
    /// <param name="username">Laserfiche username.</param>
    /// <param name="password">Plain-text password to encrypt and store.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task StoreCredentialsAsync(
        string repositoryKey,
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
