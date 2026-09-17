namespace LaserficheReports.Domain.Version;

/// <summary>
/// Single authoritative source for the LaserficheReports application version.
/// All components — API responses, the health endpoint, the home page, and the installer —
/// derive their version string from these constants.
/// </summary>
public static class LaserficheReportsVersion
{
    /// <summary>Major version. Increment for breaking changes.</summary>
    public const int Major = 1;

    /// <summary>Minor version. Increment for new features.</summary>
    public const int Minor = 0;

    /// <summary>Patch version. Increment for bug fixes.</summary>
    public const int Patch = 0;

    /// <summary>Full semantic version string, e.g. <c>1.0.0</c>.</summary>
    public static string Full => $"{Major}.{Minor}.{Patch}";

    /// <summary>Display string including the product name, e.g. <c>Laserfiche Reports v1.0.0</c>.</summary>
    public static string Display => $"Laserfiche Reports v{Full}";
}
