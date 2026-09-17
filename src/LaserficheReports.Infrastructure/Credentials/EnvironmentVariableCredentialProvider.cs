using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Credentials;

/// <summary>
/// Retrieves Laserfiche credentials from environment variables.
/// Supported variables:
/// <list type="bullet">
///   <item><term><c>LF_USERNAME</c></term><description>Laserfiche username.</description></item>
///   <item><term><c>LF_PASSWORD</c></term><description>Laserfiche password.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// This provider is used in two scenarios:
/// <list type="number">
///   <item>Development environments on any platform (including Linux/macOS/Replit).</item>
///   <item>Production deployments where the administrator explicitly opts out of DPAPI
///       by setting <c>Laserfiche:CredentialProvider = Environment</c> in
///       <c>appsettings.json</c>.</item>
/// </list>
/// </para>
/// <para>
/// <c>StoreCredentialsAsync</c> is not supported by this provider — environment variables
/// are read-only at the process level. Use the DPAPI provider for credential storage.
/// </para>
/// </remarks>
internal sealed class EnvironmentVariableCredentialProvider : ICredentialProvider
{
    private readonly ILogger<EnvironmentVariableCredentialProvider> _logger;

    /// <summary>Initialises the provider with a logger.</summary>
    public EnvironmentVariableCredentialProvider(
        ILogger<EnvironmentVariableCredentialProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<LaserficheCredential> GetCredentialsAsync(
        string repositoryKey,
        CancellationToken cancellationToken = default)
    {
        var username = Environment.GetEnvironmentVariable("LF_USERNAME");
        var password = Environment.GetEnvironmentVariable("LF_PASSWORD");

        if (string.IsNullOrWhiteSpace(username))
        {
            _logger.LogError(
                "LF_USERNAME environment variable is not set. " +
                "Set it before starting the portal.");
            throw new InvalidOperationException(
                "LF_USERNAME environment variable is not set. " +
                "Configure the variable and restart the application.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError(
                "LF_PASSWORD environment variable is not set. " +
                "Set it before starting the portal.");
            throw new InvalidOperationException(
                "LF_PASSWORD environment variable is not set. " +
                "Configure the variable and restart the application.");
        }

        _logger.LogDebug(
            "Credentials retrieved from environment variables for repository key {Key}.",
            repositoryKey);

        return Task.FromResult(new LaserficheCredential(username, password));
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown — environment variable providers do not support credential storage.
    /// Use the DPAPI provider to store credentials persistently.
    /// </exception>
    public Task StoreCredentialsAsync(
        string repositoryKey,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "The environment variable credential provider is read-only. " +
            "To store credentials persistently, configure CredentialProvider = DPAPI " +
            "and use the LF Settings page to save credentials.");
    }
}
