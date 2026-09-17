using System.Security.Cryptography;
using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Credentials;

/// <summary>
/// Stores and retrieves Laserfiche credentials using the ASP.NET Core Data Protection API.
/// This is the cross-platform equivalent of <see cref="DpapiCredentialProvider"/> and is
/// used automatically on non-Windows hosts (Linux, macOS, Replit).
/// </summary>
/// <remarks>
/// Credentials are encrypted with the application's data-protection key ring
/// (<c>IDataProtector</c> with purpose string <c>LaserficheReports.Credentials.v1</c>) and
/// written to <c>{ContentRoot}/config/credentials/{sha256(key)}.dprot</c>.
/// The key ring itself is managed by ASP.NET Core and stored in
/// <c>~/.aspnet/DataProtection-Keys/</c> on Linux by default.
/// </remarks>
internal sealed class DataProtectionCredentialProvider : ICredentialProvider
{
    private const string CredentialPurpose  = "LaserficheReports.Credentials.v1";
    private const string FileExtension      = ".dprot";

    private readonly IDataProtector _protector;
    private readonly string _credentialDirectory;
    private readonly ILogger<DataProtectionCredentialProvider> _logger;

    /// <summary>Initialises the provider and creates the credential directory if absent.</summary>
    public DataProtectionCredentialProvider(
        IDataProtectionProvider dataProtectionProvider,
        IHostEnvironment hostEnvironment,
        ILogger<DataProtectionCredentialProvider> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(CredentialPurpose);
        _credentialDirectory = Path.Combine(
            hostEnvironment.ContentRootPath, "config", "credentials");
        Directory.CreateDirectory(_credentialDirectory);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<LaserficheCredential> GetCredentialsAsync(
        string repositoryKey,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetCredentialFilePath(repositoryKey);

        if (!File.Exists(filePath))
        {
            _logger.LogError(
                "No credential file found for repository key {Key}. " +
                "Use the Settings page to configure credentials.",
                repositoryKey);
            throw new InvalidOperationException(
                $"No credentials are stored for repository '{repositoryKey}'. " +
                "Open the Settings page to configure your Laserfiche credentials.");
        }

        var encrypted = File.ReadAllText(filePath);
        var decrypted  = _protector.Unprotect(encrypted);

        var separatorIndex = decrypted.IndexOf('\n', StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            throw new InvalidOperationException(
                $"The credential file for repository '{repositoryKey}' is corrupt. " +
                "Re-save the credentials from the Settings page.");
        }

        var username = decrypted[..separatorIndex];
        var password  = decrypted[(separatorIndex + 1)..];

        _logger.LogDebug(
            "Data Protection credentials retrieved for repository key {Key}.",
            repositoryKey);
        return Task.FromResult(new LaserficheCredential(username, password));
    }

    /// <inheritdoc />
    public Task StoreCredentialsAsync(
        string repositoryKey,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_credentialDirectory);
        var payload   = $"{username}\n{password}";
        var encrypted = _protector.Protect(payload);
        File.WriteAllText(GetCredentialFilePath(repositoryKey), encrypted);

        _logger.LogInformation(
            "Credentials stored via Data Protection for repository key {Key}.",
            repositoryKey);
        return Task.CompletedTask;
    }

    /// <summary>Returns <c>true</c> when a credential file exists for the given key.</summary>
    internal bool HasCredentials(string repositoryKey) =>
        File.Exists(GetCredentialFilePath(repositoryKey));

    private string GetCredentialFilePath(string repositoryKey)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repositoryKey))).ToLowerInvariant();
        return Path.Combine(_credentialDirectory, $"{hash}{FileExtension}");
    }
}
