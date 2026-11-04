using System.Security.Cryptography;
using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.OAuth;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Web;

/// <summary>
/// LFDS single sign-on, the same flow the Dashboard uses: the Repository API
/// authorization-code endpoint with PKCE. The browser's LFDS sign-in identifies the
/// user, so Reports never sees or stores a password for these sessions.
/// </summary>
internal static class ReportSso
{
    internal const string Method = "LFDS";
    private const string PendingStateKey = "SsoPendingState";

    internal static bool Enabled(LaserficheOptions options) =>
        options.AuthenticationMode == LaserficheAuthenticationMode.LfdsSso && options.Sso.IsConfigured &&
        options.SsoAuthorizationEndpoint.Length > 0 && options.SsoCallbackUrl.Length > 0;

    internal static void MapReportSso(this WebApplication app)
    {
        app.MapGet("/launch", LaunchAsync);
        app.MapGet("/login/StartSso", StartAsync);
        app.MapGet("/login/Callback", CallbackAsync);
    }

    /// <summary>
    /// Web Client button entry point. The Web Client may have changed users since the
    /// last Reports tab, so a launch always ends the previous Reports identity first.
    /// </summary>
    internal static async Task<IResult> LaunchAsync(string? repository, HttpContext http,
        IOptionsMonitor<LaserficheOptions> options, ILaserficheAuthService auth, ISessionCredentialStore sessions,
        CancellationToken ct)
    {
        var id = repository?.Trim();
        if (!ReportSessionEndpoints.ValidRepository(id)) id = null;
        if (!Enabled(options.CurrentValue))
            return Results.Redirect(id is null ? "/" : "/?repository=" + Uri.EscapeDataString(id));
        await auth.InvalidateCurrentSessionTokensAsync();
        await sessions.ClearAsync(ct);
        http.Session.SetString("ReportsGeneration", Guid.NewGuid().ToString("N"));
        foreach (var key in new[] { "AuthenticationScopeMethod", "AuthenticationScopeSubject", "LfdsUser", "AuthenticatedLaserficheUser" })
            http.Session.Remove(key);
        return Results.Redirect("/login/StartSso" + (id is null ? "" : "?repository=" + Uri.EscapeDataString(id)));
    }

    internal static async Task<IResult> StartAsync(string? repository, bool? forceLogin, HttpContext http,
        IOptionsMonitor<LaserficheOptions> options, IRepositoryContext repositories, IOAuthStateStore states,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var logger = loggerFactory.CreateLogger("LaserficheReports.Sso");
        if (!Enabled(opts)) return Results.Redirect("/");
        if (opts.MarkdownConfigurationKeys().Count > 0 ||
            (!string.IsNullOrWhiteSpace(opts.Sso.RedirectUri) &&
             !string.Equals(opts.Sso.RedirectUri.TrimEnd('/'), opts.SsoCallbackUrl, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogError("[SSO] Invalid configuration: the callback must be exactly {CallbackUrl}.", opts.SsoCallbackUrl);
            return Failed("configuration");
        }
        var id = string.IsNullOrWhiteSpace(repository)
            ? (await repositories.GetActiveRepositoryAsync(ct)).RepositoryId
            : repository.Trim();
        if (!ReportSessionEndpoints.ValidRepository(id)) return Failed("repository");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        states.Store(state, new OAuthStateEntry
        {
            RepositoryId = id, ReturnUrl = "/", CodeVerifier = verifier,
            RedirectUri = opts.SsoCallbackUrl, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        // Binds the callback to this browser session (CSRF).
        http.Session.SetString(PendingStateKey, state);
        var url = AuthorizationUrl(opts.SsoAuthorizationEndpoint, id, state,
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), opts.SsoCallbackUrl, forceLogin == true);
        logger.LogInformation("[SSO] Redirecting to Repository API authorize endpoint for repository {Repository}; ForceLogin={ForceLogin}.",
            id, forceLogin == true);
        return Results.Redirect(url);
    }

    internal static async Task<IResult> CallbackAsync(string? code, string? state, string? error, HttpContext http,
        IOptionsMonitor<LaserficheOptions> options, IRepositoryContext repositories, IOAuthStateStore states,
        ILaserficheAuthService auth, ISessionCredentialStore sessions, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("LaserficheReports.Sso");
        var pending = http.Session.GetString(PendingStateKey);
        http.Session.Remove(PendingStateKey);
        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("[SSO] The Repository API returned {Error}.", error);
            return Failed("provider");
        }
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || pending is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pending), Encoding.UTF8.GetBytes(state)))
        {
            logger.LogWarning("[SSO] Callback rejected: missing code or state not issued to this session.");
            return Failed("state");
        }
        // One-time: expired, replayed or unknown states return null.
        var entry = states.TryConsume(state);
        if (entry is null || string.IsNullOrWhiteSpace(entry.CodeVerifier) || !ReportSessionEndpoints.ValidRepository(entry.RepositoryId))
            return Failed("state");

        // Sign in within a provisional scope, as password login does: a failed sign-in
        // leaves the previous account, token cache and repository selection intact.
        string[] keys = ["ActiveRepositoryId", "ReportsGeneration", "AuthenticationScopeMethod", "AuthenticationScopeSubject",
            "LfdsUser", "AuthenticatedLaserficheUser"];
        var previous = keys.ToDictionary(key => key, key => http.Session.GetString(key));
        http.Session.SetString("ActiveRepositoryId", entry.RepositoryId);
        http.Session.SetString("AuthenticationScopeMethod", Method);
        http.Session.SetString("AuthenticationScopeSubject", Guid.NewGuid().ToString("N"));
        http.Session.Remove("LfdsUser");
        http.Session.Remove("AuthenticatedLaserficheUser");
        var committed = false;
        try
        {
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            bool exchanged;
            try
            {
                exchanged = await auth.ExchangeAuthorizationCodeAsync(repository, code, entry.CodeVerifier,
                    entry.RedirectUri, options.CurrentValue.Sso.ClientId, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "[SSO] Token exchange failed for repository {Repository}.", repository.RepositoryId);
                return Failed("exchange");
            }
            if (!exchanged) return Failed("exchange");

            // The exchange records the token's user name; SSO sessions keep no password.
            await sessions.ClearAsync(ct);
            var user = http.Session.GetString("AuthenticatedLaserficheUser");
            http.Session.SetString("LfdsUser", string.IsNullOrWhiteSpace(user) ? "LFDS" : user);
            http.Session.SetString("ReportsGeneration", Guid.NewGuid().ToString("N"));
            committed = true;
            logger.LogInformation("[SSO] Signed in {User} via LFDS for repository {Repository}.",
                http.Session.GetString("LfdsUser"), repository.RepositoryId);
            return Results.Redirect("/");
        }
        finally
        {
            if (!committed)
            {
                try { await auth.InvalidateCurrentSessionTokensAsync(); }
                finally
                {
                    foreach (var key in keys)
                        if (previous[key] is { } value) http.Session.SetString(key, value);
                        else http.Session.Remove(key);
                }
            }
        }
    }

    internal static string AuthorizationUrl(string endpoint, string repositoryId, string state, string challenge,
        string redirectUri, bool forceLogin) =>
        endpoint + "?repositoryId=" + Uri.EscapeDataString(repositoryId) + "&response_type=code" +
        "&redirect_uri=" + Uri.EscapeDataString(redirectUri) + "&state=" + Uri.EscapeDataString(state) +
        "&code_challenge=" + Uri.EscapeDataString(challenge) + "&code_challenge_method=S256" +
        (forceLogin ? "&prompt=login" : "");

    private static IResult Failed(string reason) => Results.Redirect("/?sso_error=" + reason);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
