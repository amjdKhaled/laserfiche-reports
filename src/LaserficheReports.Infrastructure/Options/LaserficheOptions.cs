using System.ComponentModel.DataAnnotations;
using LaserficheReports.Infrastructure.OAuth;

namespace LaserficheReports.Infrastructure.Options;

/// <summary>
/// Selects which credential provider implementation is used at runtime.
/// Configured via <c>Laserfiche:CredentialProvider</c> in <c>appsettings.json</c>.
/// </summary>
public enum CredentialProviderType
{
    /// <summary>
    /// Windows Data Protection API (DPAPI). Default for production on Windows Server.
    /// Credentials are encrypted with the machine key; usable only on the same machine.
    /// </summary>
    DPAPI,

    /// <summary>
    /// Environment variables <c>LF_USERNAME</c> and <c>LF_PASSWORD</c>.
    /// Supported on all platforms. Recommended for development and non-Windows environments.
    /// </summary>
    Environment
}

public enum LaserficheAuthenticationMode
{
    RepositoryPassword,
    LfdsSso,
}

/// <summary>
/// Configuration options for the Laserfiche Repository API connection.
/// Bound from the <c>Laserfiche</c> section in <c>appsettings.json</c> at startup.
/// </summary>
/// <remarks>
/// Credentials are intentionally absent from this class. They are sourced at runtime
/// by the registered <see cref="ICredentialProvider"/> implementation, never stored
/// in configuration files as plain text.
/// </remarks>
public sealed class LaserficheOptions
{
    public const string SectionName = "Laserfiche";
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// Base URL of the Laserfiche API Server, e.g. <c>https://your-lf-server.example.com</c>.
    /// Do not include the <c>/LFRepositoryAPI</c> path here.
    /// </summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Selects direct Repository API password login or LFDS PKCE SSO.</summary>
    public LaserficheAuthenticationMode AuthenticationMode { get; set; } =
        LaserficheAuthenticationMode.RepositoryPassword;

    /// <summary>Public browser origin of the Reports application, used for every OAuth callback.</summary>
    public string ApplicationBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional fallback repository identifier. Per-session repository selection wins.
    /// </summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>Human-readable repository label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>IIS virtual directory path where the Repository API is installed.</summary>
    public string ApiBasePath { get; set; } = "/LFRepositoryAPI";

    public const string ApiVersionAuto = "Auto";

    /// <summary>
    /// Configured API version: Auto, v1, or v2. URL builders use
    /// <see cref="EffectiveApiVersion"/> rather than this raw value.
    /// </summary>
    public string ApiVersion { get; set; } = ApiVersionAuto;

    /// <summary>API version discovered by the background detection service.</summary>
    public string DetectedApiVersion { get; set; } = string.Empty;

    public string EffectiveApiVersion =>
        !IsAutoApiVersion ? ApiVersion.Trim()
        : !string.IsNullOrWhiteSpace(DetectedApiVersion) ? DetectedApiVersion.Trim()
        : "v1";

    public bool IsAutoApiVersion =>
        string.IsNullOrWhiteSpace(ApiVersion) ||
        string.Equals(ApiVersion.Trim(), ApiVersionAuto, StringComparison.OrdinalIgnoreCase);

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>
    /// Effective Repository API request timeout. Older installations can retain the
    /// former 30-second value in laserfiche.runtime.json; keep those installations
    /// working without requiring a manual settings-file migration.
    /// </summary>
    public int EffectiveTimeoutSeconds => Math.Max(TimeoutSeconds, DefaultTimeoutSeconds);

    public CredentialProviderType CredentialProvider { get; set; } =
        OperatingSystem.IsWindows()
            ? CredentialProviderType.DPAPI
            : CredentialProviderType.Environment;

    /// <summary>
    /// Optional administrator fallback for repository root discovery.
    /// A value of 0 (the default) means no assumed root ID: the application discovers
    /// the authoritative root with Entries/ByPath. Set a positive value only when an
    /// installation cannot perform dynamic root discovery and the administrator has
    /// verified the exact root entry ID.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int RootEntryId { get; set; } = 0;

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? RepositoryId : DisplayName;

    /// <summary>LFDS OAuth2 / Authorization Code SSO settings.</summary>
    public LaserficheOAuthOptions Sso { get; set; } = new();

    /// <summary>
    /// Repository API endpoint that initiates the V2 authorization-code flow.
    /// </summary>
    public string SsoAuthorizationEndpoint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ServerUrl))
                return string.Empty;

            var serverUrl = ServerUrl.TrimEnd('/');
            var apiBasePath = "/" + ApiBasePath.Trim('/');

            var apiRoot = serverUrl.EndsWith(apiBasePath, StringComparison.OrdinalIgnoreCase)
                ? serverUrl
                : serverUrl + apiBasePath;

            return $"{apiRoot}/v2/Authorize";
        }
    }

    public string SsoCallbackUrl => string.IsNullOrWhiteSpace(ApplicationBaseUrl)
        ? string.Empty
        : $"{ApplicationBaseUrl.TrimEnd('/')}/login/Callback";

    public string GetSsoTokenEndpoint(string repositoryId)
    {
        var authorize = SsoAuthorizationEndpoint;
        if (!authorize.EndsWith("Authorize", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return authorize[..^"Authorize".Length] + "Repositories/" +
            Uri.EscapeDataString(repositoryId) + "/Token";
    }

    /// <summary>Detects pasted Markdown links, which are never valid URL settings.</summary>
    public IReadOnlyList<string> MarkdownConfigurationKeys()
    {
        static bool Invalid(string? value) => value?.IndexOfAny(['[', ']', '(', ')']) >= 0;
        var invalid = new List<string>();
        if (Invalid(ServerUrl)) invalid.Add("Laserfiche:ServerUrl");
        if (Invalid(ApiBasePath)) invalid.Add("Laserfiche:ApiBasePath");
        if (Invalid(ApplicationBaseUrl)) invalid.Add("Laserfiche:ApplicationBaseUrl");
        if (Invalid(Sso.LfdsBaseUrl)) invalid.Add("Laserfiche:Sso:LfdsBaseUrl");
        if (Invalid(Sso.RedirectUri)) invalid.Add("Laserfiche:Sso:RedirectUri");
        return invalid;
    }
}
