namespace LaserficheReports.Infrastructure.Configuration;

/// <summary>
/// Central definition of the Reports application's writable configuration home.
/// </summary>
/// <remarks>
/// <para>
/// Phase-1 configuration architecture: there is exactly ONE writable configuration
/// location — <c>%ProgramData%\LaserficheReports\</c> (resolved dynamically via
/// <see cref="Environment.SpecialFolder.CommonApplicationData"/>, never hardcoded
/// to a drive or machine-specific path).  Configuration is layered in this order,
/// last-wins:
/// </para>
/// <list type="number">
///   <item><c>appsettings.json</c> — structural defaults only, read-only, ships with the app.</item>
///   <item><c>&lt;ContentRoot&gt;\config\laserfiche.json</c> — LEGACY writable file from
///         pre-Phase-1 installations; still read for backward compatibility, never written.</item>
///   <item><c>%ProgramData%\LaserficheReports\laserfiche.config.json</c> — written by the installer
///         wizard (WriteConfigAction).  Infrastructure settings only (ServerUrl, ApiBasePath,
///         ApiVersion, TimeoutSeconds, CredentialProvider).  NEVER contains repository
///         identifiers — repository selection is runtime session context.</item>
///   <item><c>%ProgramData%\LaserficheReports\laserfiche.runtime.json</c> — written by the runtime
///         Settings page.  Administrator overrides entered after installation.  The installer
///         never creates, modifies, or removes this file, so admin-entered settings survive
///         repair, upgrade, and reinstall.</item>
/// </list>
/// <para>
/// The application never requires write access inside its install directory
/// (e.g. <c>Program Files</c>).  On platforms/environments where the ProgramData
/// directory cannot be created (non-Windows development hosts), the runtime
/// settings file falls back to the legacy content-root location, which is
/// writable in development scenarios by definition.
/// </para>
/// </remarks>
public static class ReportsConfigPaths
{
    /// <summary>Product configuration folder name under ProgramData.</summary>
    public const string ProductFolderName = "LaserficheReports";

    /// <summary>Installer-written connection configuration file name.</summary>
    public const string InstallerConfigFileName = "laserfiche.config.json";

    /// <summary>Settings-page-written runtime override file name.</summary>
    public const string RuntimeConfigFileName = "laserfiche.runtime.json";

    /// <summary>Legacy writable file name under &lt;ContentRoot&gt;\config\ (pre-Phase-1).</summary>
    public const string LegacyRuntimeConfigFileName = "laserfiche.json";

    /// <summary>
    /// The machine-wide Laserfiche Reports configuration directory:
    /// <c>%ProgramData%\LaserficheReports</c> (or the platform equivalent of CommonApplicationData).
    /// </summary>
    public static string ProgramDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProductFolderName);

    /// <summary>Full path of the installer-written configuration file.</summary>
    public static string InstallerConfigPath =>
        Path.Combine(ProgramDataDirectory, InstallerConfigFileName);

    /// <summary>Full path of the Settings-page runtime override file.</summary>
    public static string RuntimeConfigPath =>
        Path.Combine(ProgramDataDirectory, RuntimeConfigFileName);

    /// <summary>Full path of the legacy content-root writable file (read-compat + dev fallback).</summary>
    public static string GetLegacyRuntimeConfigPath(string contentRootPath) =>
        Path.Combine(contentRootPath, "config", LegacyRuntimeConfigFileName);

    /// <summary>
    /// Resolves the file the Settings page should WRITE to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows (the production platform) this is ALWAYS
    /// <see cref="RuntimeConfigPath"/> — the app never silently redirects writes
    /// into its install directory (Program Files).  If ProgramData is not writable
    /// for the process identity, the save operation fails loudly with the real
    /// exception so the missing ACL is diagnosed, not masked.
    /// </para>
    /// <para>
    /// On non-Windows hosts (development), the platform CommonApplicationData
    /// location (e.g. <c>/usr/share</c>) is typically read-only and not the
    /// deployment target; when it is not writable the legacy content-root file
    /// is used so development keeps working unchanged.
    /// </para>
    /// </remarks>
    public static string ResolveWritableRuntimeConfigPath(string contentRootPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeConfigPath;
        }

        try
        {
            Directory.CreateDirectory(ProgramDataDirectory);

            // Verify writability explicitly; CreateDirectory succeeding does not
            // guarantee write permission for the current process identity.
            var probe = Path.Combine(ProgramDataDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return RuntimeConfigPath;
        }
        catch (Exception)
        {
            return GetLegacyRuntimeConfigPath(contentRootPath);
        }
    }
}
