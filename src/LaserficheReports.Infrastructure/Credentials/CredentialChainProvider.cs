using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Credentials;

/// <summary>
/// Credential provider that tries a primary secure store first and automatically
/// falls back to environment variables when no stored credentials exist.
/// </summary>
/// <remarks>
/// <para>
/// On Windows the primary store is DPAPI (<see cref="DpapiCredentialProvider"/>).
/// On non-Windows the primary store is ASP.NET Core Data Protection
/// (<see cref="DataProtectionCredentialProvider"/>).
/// The fallback in both cases is <see cref="EnvironmentVariableCredentialProvider"/>.
/// </para>
/// <para>
/// Credential writes (<see cref="StoreCredentialsAsync"/>) always go to the primary
/// store; the fallback is read-only.
/// </para>
/// </remarks>
internal sealed class CredentialChainProvider : ICredentialProvider
{
    private readonly ICredentialProvider _primary;
    private readonly ICredentialProvider _fallback;
    private readonly ILogger<CredentialChainProvider> _logger;

    /// <summary>
    /// Initialises the chain with a writable primary store and a read-only fallback.
    /// </summary>
    internal CredentialChainProvider(
        ICredentialProvider primary,
        ICredentialProvider fallback,
        ILogger<CredentialChainProvider> logger)
    {
        _primary  = primary;
        _fallback = fallback;
        _logger   = logger;
    }

    /// <inheritdoc />
    public async Task<LaserficheCredential> GetCredentialsAsync(
        string repositoryKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _primary.GetCredentialsAsync(repositoryKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException primaryEx)
        {
            _logger.LogInformation(
                "Primary credential store has no credentials for {Key} ({Reason}). " +
                "Falling back to environment variables.",
                repositoryKey,
                primaryEx.Message);

            return await _fallback.GetCredentialsAsync(repositoryKey, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes always target the primary store. The fallback is read-only and never written.
    /// </remarks>
    public Task StoreCredentialsAsync(
        string repositoryKey,
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        _primary.StoreCredentialsAsync(repositoryKey, username, password, cancellationToken);
}
