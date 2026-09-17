using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Credentials;

/// <summary>
/// Stores and retrieves Laserfiche credentials using the Windows Data Protection API (DPAPI).
/// Credentials are encrypted with the machine-scope key — they can only be decrypted on
/// the same Windows machine that encrypted them.
/// </summary>
/// <remarks>
/// <para>
/// This provider is the default for production deployments on Windows Server.
/// It is the only credential provider that supports <c>StoreCredentialsAsync</c>.
/// The Settings page uses this method to persist credentials after the administrator
/// enters them in the portal UI.
/// </para>
/// <para>
/// Encrypted blobs are written to <c>%ProgramData%\Dashboard\credentials\</c>.
/// One file per repository key. File names are SHA-256 hashes of the repository key
/// to avoid exposing key names in the filesystem.
/// </para>
/// <para>
/// Backward-compatibility: when reading, the provider also checks the legacy
/// <c>%ProgramData%\LaserficheReports\credentials\</c> path so that credentials saved by
/// older installations continue to work without re-entry.  Writes always go to
/// the new <c>Dashboard</c> path.
/// </para>
/// <para>
/// Not supported on non-Windows platforms. The DI registration in
/// <c>ServiceCollectionExtensions</c> automatically falls back to
/// <see cref="EnvironmentVariableCredentialProvider"/> on non-Windows.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DpapiCredentialProvider : ICredentialProvider
{
    private readonly ILogger<DpapiCredentialProvider> _logger;

    private static readonly string ProgramData =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>Primary credential directory (current product name).</summary>
    private static readonly string CredentialDirectory =
        Path.Combine(ProgramData, "Dashboard", "credentials");

    /// <summary>
    /// Legacy credential directory kept for backward-compatibility.
    /// Credentials written by earlier installations (when the product was named LaserficheReports)
    /// are readable from this path; new credentials are never written here.
    /// </summary>
    private static readonly string LegacyCredentialDirectory =
        Path.Combine(ProgramData, "LaserficheReports", "credentials");

    /// <summary>Initialises the provider, creating the credential directory if necessary.</summary>
    public DpapiCredentialProvider(ILogger<DpapiCredentialProvider> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(CredentialDirectory);
    }

    /// <inheritdoc />
    public Task<LaserficheCredential> GetCredentialsAsync(
        string repositoryKey,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetCredentialFilePath(repositoryKey);

        if (!File.Exists(filePath))
        {
            // Backward-compat: check the legacy LaserficheReports path for credentials saved by
            // older installations.  If found, we read them but do not migrate them here;
            // the next StoreCredentialsAsync call will write them to the new path.
            var legacyPath = GetLegacyCredentialFilePath(repositoryKey);
            if (File.Exists(legacyPath))
            {
                _logger.LogInformation(
                    "Reading DPAPI credentials from legacy path for repository key {Key}. " +
                    "Re-save from the Settings page to migrate to the new credentials directory.",
                    repositoryKey);
                filePath = legacyPath;
            }
            else
            {
                _logger.LogError(
                    "No DPAPI-encrypted credential file found for repository key {Key}. " +
                    "Use the Settings page to save credentials.",
                    repositoryKey);
                throw new InvalidOperationException(
                    $"No credentials are stored for repository '{repositoryKey}'. " +
                    "Open the Settings page and save the Laserfiche credentials.");
            }
        }

        var encryptedBytes = File.ReadAllBytes(filePath);
        var decryptedBytes = ProtectedData.Unprotect(
            encryptedBytes,
            null,
            DataProtectionScope.LocalMachine);

        var payload = Encoding.UTF8.GetString(decryptedBytes);
        var separatorIndex = payload.IndexOf('\n', StringComparison.Ordinal);

        if (separatorIndex < 0)
        {
            _logger.LogError(
                "Corrupt credential file for repository key {Key}: missing newline separator.",
                repositoryKey);
            throw new InvalidOperationException(
                $"The credential file for repository '{repositoryKey}' is corrupt. " +
                "Re-save the credentials from the LF Settings page.");
        }

        var username = payload[..separatorIndex];
        var password = payload[(separatorIndex + 1)..];

        _logger.LogDebug(
            "DPAPI credentials retrieved successfully for repository key {Key}.",
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
        var payload = $"{username}\n{password}";
        var plaintextBytes = Encoding.UTF8.GetBytes(payload);
        var encryptedBytes = ProtectedData.Protect(
            plaintextBytes,
            null,
            DataProtectionScope.LocalMachine);

        var filePath = GetCredentialFilePath(repositoryKey);
        File.WriteAllBytes(filePath, encryptedBytes);

        _logger.LogInformation(
            "DPAPI credentials stored for repository key {Key}.",
            repositoryKey);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the primary (Dashboard) filesystem path for the specified repository key.
    /// The filename is the lowercase hex-encoded SHA-256 hash of the key.
    /// </summary>
    private static string GetCredentialFilePath(string repositoryKey)
    {
        var filename = HashFilename(repositoryKey);
        return Path.Combine(CredentialDirectory, filename);
    }

    /// <summary>
    /// Returns the legacy (LaserficheReports) filesystem path for the specified repository key.
    /// Used only for backward-compatible reads of credentials saved by older installations.
    /// </summary>
    private static string GetLegacyCredentialFilePath(string repositoryKey)
    {
        var filename = HashFilename(repositoryKey);
        return Path.Combine(LegacyCredentialDirectory, filename);
    }

    private static string HashFilename(string repositoryKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(repositoryKey);
        var hash = SHA256.HashData(keyBytes);
        return Convert.ToHexString(hash).ToLowerInvariant() + ".dpapi";
    }
}
