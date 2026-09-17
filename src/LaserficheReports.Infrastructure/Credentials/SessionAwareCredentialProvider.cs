using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Credentials;

/// <summary>
/// Composite <see cref="ICredentialProvider"/> that serves credentials from the
/// current HTTP session (Desktop Client login flow) before falling back to the
/// disk-based credential chain (Settings page / environment variables).
/// </summary>
/// <remarks>
/// <para>
/// Priority order on reads:
/// <list type="number">
///   <item>Session credentials — established by the Login page for the current WebView2 popup.</item>
///   <item>Disk credential chain — DPAPI (Windows) or Data Protection (non-Windows) + env-var fallback.</item>
/// </list>
/// </para>
/// <para>
/// Writes always go to the disk chain so that Settings-page credential saves are not
/// affected by the session flow.
/// </para>
/// <para>
/// Registered as a singleton. Both <see cref="ISessionCredentialStore"/> and the
/// disk chain are also singleton-safe.
/// </para>
/// </remarks>
internal sealed class SessionAwareCredentialProvider : ICredentialProvider
{
    private readonly ISessionCredentialStore _sessionStore;
    private readonly ICredentialProvider     _diskChain;
    private readonly ILogger<SessionAwareCredentialProvider> _logger;

    /// <summary>Initialises the provider with the session store and disk chain fallback.</summary>
    public SessionAwareCredentialProvider(
        ISessionCredentialStore sessionStore,
        ICredentialProvider     diskChain,
        ILogger<SessionAwareCredentialProvider> logger)
    {
        _sessionStore = sessionStore;
        _diskChain    = diskChain;
        _logger       = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns session credentials when present (Desktop Client login flow);
    /// falls back to the disk chain for direct browser / Settings-configured access.
    /// </remarks>
    public async Task<LaserficheCredential> GetCredentialsAsync(
        string repositoryKey,
        CancellationToken cancellationToken = default)
    {
        var sessionCred = await _sessionStore.TryGetAsync(cancellationToken).ConfigureAwait(false);

        if (sessionCred is not null)
        {
            _logger.LogDebug(
                "Credentials for repository key {Key} resolved from session (Desktop Client login).",
                repositoryKey);
            return sessionCred;
        }

        _logger.LogDebug(
            "No session credentials for repository key {Key}. Falling back to disk chain.",
            repositoryKey);

        return await _diskChain.GetCredentialsAsync(repositoryKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Credential writes always target the disk chain. Session credentials are established
    /// exclusively through the Login page, not through <see cref="StoreCredentialsAsync"/>.
    /// </remarks>
    public Task StoreCredentialsAsync(
        string repositoryKey,
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        _diskChain.StoreCredentialsAsync(repositoryKey, username, password, cancellationToken);
}
