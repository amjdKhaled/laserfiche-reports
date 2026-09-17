namespace LaserficheReports.Infrastructure.OAuth;

/// <summary>
/// LFDS OAuth2 / Authorization Code configuration for SSO.
/// Nested under <c>Laserfiche:Sso</c> in <c>appsettings.json</c> or the installer
/// config file (<c>%ProgramData%\LaserficheReports\laserfiche.config.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// Leave <see cref="LfdsBaseUrl"/> empty to disable SSO entirely — users will sign
/// in with the standard username/password Login form.
/// </para>
/// <para>
/// The Laserfiche API Server must be configured with
/// <c>LFDSSTSBaseUrl</c> pointing to the same LFDS instance, and
/// <c>WhitelistedRedirectUris</c> must include the Reports application callback URL:
/// <c>{scheme}://{host}/Login/Callback</c>.
/// </para>
/// </remarks>
public sealed class LaserficheOAuthOptions
{
    /// <summary>
    /// Base URL of the LFDS (Laserfiche Directory Server) STS,
    /// e.g. <c>https://your-lf-server.example.com/LFDS</c>.
    /// Leave empty to disable SSO; users will sign in with username/password.
    /// </summary>
    public string LfdsBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 client ID registered in LFDS for this Reports application.
    /// Must match the value registered on the Laserfiche API Server.
    /// Defaults to <c>LaserficheReports</c>.
    /// </summary>
    public string ClientId { get; set; } = "LaserficheReports";

    /// <summary>
    /// Optional fixed OAuth redirect URI.
    /// When empty (the default), the redirect URI is built dynamically from the
    /// incoming request — <c>{scheme}://{host}/Login/Callback</c> — so the app
    /// works correctly behind load balancers, name changes, and multi-homed servers.
    /// Set this explicitly when a reverse proxy changes the apparent host or scheme.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// True when LFDS SSO is configured (<see cref="LfdsBaseUrl"/> is non-empty).
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(LfdsBaseUrl);

}
