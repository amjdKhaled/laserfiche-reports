using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Manages Laserfiche Bearer token acquisition, caching, and invalidation.
/// Tokens are cached per repository and proactively refreshed before expiry.
/// </summary>
/// <remarks>
/// <para>
/// Callers never see credentials — they receive only an opaque Bearer token string.
/// Token acquisition calls <see cref="ICredentialProvider"/> internally; credentials
/// are not held in memory beyond the HTTP request that uses them.
/// </para>
/// <para>
/// Interactive token cache entries are scoped by repository and Dashboard session,
/// so different users of the same repository never share a token lifecycle.
/// </para>
/// </remarks>
public interface ILaserficheAuthService
{
    /// <summary>
    /// Returns a valid Bearer token for the specified repository, acquiring a new one
    /// if the cache is empty or the cached token is within 60 seconds of expiry.
    /// </summary>
    /// <param name="repository">The repository to authenticate against.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns>A raw Bearer token string, without the <c>Bearer </c> prefix.</returns>
    Task<string> GetTokenAsync(
        RepositoryDescriptor repository,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the cached token for the specified repository, forcing re-authentication
    /// on the next call to <see cref="GetTokenAsync"/>.
    /// Call this after receiving a 401 response from the API to recover from token expiry.
    /// </summary>
    /// <param name="repository">The repository whose cached token should be discarded.</param>
    Task InvalidateTokenAsync(RepositoryDescriptor repository);

    /// <summary>
    /// Removes ALL cached tokens belonging to the current HTTP session, across every
    /// repository. Call this on Sign Out / Change Account so a token acquired by the
    /// previous account can never be reused by the next login in the same browser
    /// session (the ASP.NET session id survives sign-out).
    /// Outside an HTTP context this is a no-op.
    /// </summary>
    Task InvalidateCurrentSessionTokensAsync();

    /// <summary>
    /// Attempts to authenticate against the specified repository using the supplied
    /// credentials without consulting <see cref="ICredentialProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On success the acquired Bearer token is written to the memory cache under the same
    /// key used by <see cref="GetTokenAsync"/>, so subsequent domain-service calls will
    /// find a warm cache without prompting for credentials again.
    /// </para>
    /// <para>
    /// Returns <c>false</c> for credential failures (HTTP 4xx).
    /// Infrastructure errors (network failures, HTTP 5xx) are propagated as exceptions.
    /// The password is never logged.
    /// </para>
    /// </remarks>
    /// <param name="repository">The repository to authenticate against.</param>
    /// <param name="username">Laserfiche username.</param>
    /// <param name="password">Plain-text password (may be an empty string).</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the Laserfiche API accepted the credentials;
    /// <c>false</c> when it rejected them.
    /// </returns>
    Task<bool> TryAuthenticateAsync(
        RepositoryDescriptor repository,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges an LFDS OAuth2 authorization code for a Bearer token using the
    /// PKCE Authorization Code flow, then stores the token in the cache under the
    /// same key as <see cref="GetTokenAsync"/> so that subsequent domain-service
    /// calls find a warm cache without requiring a second authentication round-trip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The V2 token endpoint is always used for the exchange, even when the configured
    /// <c>EffectiveApiVersion</c> is <c>v1</c>.  The resulting Bearer token is accepted
    /// by both V1 and V2 Laserfiche Repository API resource endpoints because token
    /// validation is performed by the same Laserfiche Server regardless of API path version.
    /// </para>
    /// <para>
    /// The authorization <paramref name="code"/> and <paramref name="codeVerifier"/> are
    /// never logged.  Only the token URL and repository ID are recorded.
    /// </para>
    /// <para>
    /// Returns <c>false</c> for rejection responses (HTTP 4xx — code expired, already
    /// used, bad verifier, etc.).  Infrastructure errors (network failures, HTTP 5xx)
    /// are propagated as exceptions.
    /// </para>
    /// </remarks>
    /// <param name="repository">Repository the code was issued for.</param>
    /// <param name="code">The authorization code received from LFDS. Never logged.</param>
    /// <param name="codeVerifier">PKCE code verifier for this flow. Never logged.</param>
    /// <param name="redirectUri">Exact redirect URI used in the authorization request.</param>
    /// <param name="clientId">OAuth2 client ID.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns><c>true</c> on success; <c>false</c> when LFDS rejected the code.</returns>
    Task<bool> ExchangeAuthorizationCodeAsync(
        RepositoryDescriptor repository,
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        CancellationToken cancellationToken = default);
}
