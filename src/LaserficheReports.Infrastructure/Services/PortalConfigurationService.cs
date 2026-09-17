using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Persists portal-level connection settings to a writable JSON file and reports
/// the current credential storage status. Registered as a singleton.
/// </summary>
internal sealed class PortalConfigurationService : IPortalConfigurationService
{
    private const string DefaultRepositoryKey        = "default";
    private const string DpapiFileExtension          = ".dpapi";
    private const string DataProtectionFileExtension = ".dprot";

    private readonly string _configFilePath;
    private readonly string _dpRotCredentialDirectory;
    private readonly ILogger<PortalConfigurationService> _logger;

    /// <summary>
    /// Serialises all runtime-config writers (admin Settings saves, API-version
    /// detection). The file write itself is atomic (temp + move), but the
    /// read-merge-write cycle is not — without this lock two concurrent saves
    /// could silently lose each other's fields.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions WriteOptions =
        new() { WriteIndented = true };

    /// <summary>Initialises the service and resolves the writable configuration file.</summary>
    /// <remarks>
    /// The Settings page writes to <c>%ProgramData%\LaserficheReports\laserfiche.runtime.json</c>
    /// (resolved dynamically — never a hardcoded path).  The application never requires
    /// write access inside its install directory; when the ProgramData directory is not
    /// writable (non-Windows development hosts), the legacy content-root file is used.
    /// </remarks>
    public PortalConfigurationService(
        IHostEnvironment hostEnvironment,
        ILogger<PortalConfigurationService> logger)
    {
        _configFilePath = ReportsConfigPaths
            .ResolveWritableRuntimeConfigPath(hostEnvironment.ContentRootPath);
        _dpRotCredentialDirectory = Path.Combine(
            hostEnvironment.ContentRootPath, "config", "credentials");
        _logger = logger;

        _logger.LogInformation(
            "Portal settings will be saved to {ConfigFilePath}.", _configFilePath);
    }

    /// <inheritdoc />
    public async Task SaveConnectionSettingsAsync(
        string serverUrl,
        string repositoryId,
        string displayName,
        string apiBasePath,
        string apiVersion,
        int    rootEntryId,
        int    timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await MutateConfigAsync(laserfiche =>
        {
            laserfiche["ServerUrl"]      = serverUrl.TrimEnd('/');
            laserfiche["RepositoryId"]   = repositoryId;
            laserfiche["DisplayName"]    = displayName;
            laserfiche["ApiBasePath"]    = apiBasePath;
            laserfiche["ApiVersion"]     = apiVersion;
            laserfiche["RootEntryId"]    = rootEntryId;
            laserfiche["TimeoutSeconds"] = timeoutSeconds;

            // Connection changed or the version was pinned/re-set — any previously
            // detected version may be stale for the new server; detection re-runs.
            laserfiche["DetectedApiVersion"] = string.Empty;
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Connection settings saved: ServerUrl={ServerUrl}, RepositoryId={RepositoryId}, " +
            "ApiBasePath={ApiBasePath}, ApiVersion={ApiVersion}.",
            serverUrl, repositoryId, apiBasePath, apiVersion);
    }

    /// <inheritdoc />
    public async Task SaveDetectedApiVersionAsync(string detectedVersion, CancellationToken cancellationToken = default)
    {
        await MutateConfigAsync(
            laserfiche => laserfiche["DetectedApiVersion"] = detectedVersion,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Detected Laserfiche API version persisted: {DetectedVersion}.", detectedVersion);
    }

    /// <summary>
    /// Serialises all writers of the runtime settings file (admin Settings saves,
    /// API-version detection) so concurrent read-merge-write cycles cannot lose
    /// each other's updates, then applies <paramref name="mutate"/> to the
    /// <c>Laserfiche</c> section and writes the file atomically.
    /// </summary>
    private async Task MutateConfigAsync(Action<JsonObject> mutate, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MutateConfigLockedAsync(mutate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task MutateConfigLockedAsync(Action<JsonObject> mutate, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configFilePath)!;
        Directory.CreateDirectory(directory);

        // Merge into the existing file (if any) so fields managed elsewhere —
        // e.g. CredentialProvider — are preserved rather than silently dropped.
        JsonObject root;
        if (File.Exists(_configFilePath))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(_configFilePath, cancellationToken)
                    .ConfigureAwait(false);
                root = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Existing settings file {Path} is not valid JSON; it will be rewritten.",
                    _configFilePath);
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["Laserfiche"] is not JsonObject laserfiche)
        {
            laserfiche = new JsonObject();
            root["Laserfiche"] = laserfiche;
        }

        mutate(laserfiche);

        var json = root.ToJsonString(WriteOptions);

        // Atomic replacement: write to a temp file in the same directory, then move
        // over the target.  Readers (including the configuration reload watcher)
        // never observe a partially written file.
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_configFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

            // The reload watcher (or an antivirus scanner) may briefly hold the
            // target open; retry the atomic replacement a few times before failing.
            const int maxAttempts = 4;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, _configFilePath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(100 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { /* best-effort cleanup */ }
            }
        }
    }

    /// <inheritdoc />
    public bool HasSavedCredentials()
    {
        var hash = GetKeyHash(DefaultRepositoryKey);

        // Windows: DPAPI encrypted file — checked FIRST so a stale non-Windows
        // Data Protection file left in the content root cannot mask the absence
        // of real DPAPI credentials on a production Windows host.
        // Primary location is %ProgramData%\LaserficheReports\credentials (matches
        // DpapiCredentialProvider and the installer-prepared ACL'd directory);
        // the legacy %ProgramData%\LaserficheReports\credentials path is checked for
        // credentials saved by pre-rename installations.
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            var fileName = $"{hash}{DpapiFileExtension}";

            if (File.Exists(Path.Combine(
                    ReportsConfigPaths.ProgramDataDirectory, "credentials", fileName)))
                return true;

            if (File.Exists(Path.Combine(programData, "LaserficheReports", "credentials", fileName)))
                return true;

            return false;
        }

        // Non-Windows (development): ASP.NET Data Protection encrypted file.
        var dpRotPath = Path.Combine(
            _dpRotCredentialDirectory, $"{hash}{DataProtectionFileExtension}");
        return File.Exists(dpRotPath);
    }

    /// <inheritdoc />
    public bool HasEnvironmentVariableCredentials() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LF_USERNAME")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LF_PASSWORD"));

    private static string GetKeyHash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
