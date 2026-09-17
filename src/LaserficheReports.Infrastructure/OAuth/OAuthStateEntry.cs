namespace LaserficheReports.Infrastructure.OAuth;

/// <summary>
/// Represents a single LFDS OAuth2 Authorization Code flow in progress.
/// Stored server-side in <see cref="IOAuthStateStore"/>, keyed by a cryptographically
/// random state string that is never exposed after the initial browser redirect.
/// </summary>
public sealed class OAuthStateEntry
{
    /// <summary>Laserfiche repository ID this authorization is targeting.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>Local URL to redirect to after successful authentication.</summary>
    public string ReturnUrl { get; init; } = "/";

    /// <summary>
    /// PKCE code verifier (random, 43–128 character ASCII string).
    /// Never sent to the browser; sent only during the token exchange so LFDS can
    /// verify it against the SHA-256 code challenge embedded in the authorization request.
    /// </summary>
    public string CodeVerifier { get; init; } = string.Empty;

    /// <summary>
    /// The exact redirect URI used in the authorization request.
    /// LFDS validates that the token exchange provides the identical value.
    /// </summary>
    public string RedirectUri { get; init; } = string.Empty;

    /// <summary>
    /// UTC time after which this state entry is invalid.
    /// State entries expire after ten minutes to limit the CSRF attack window.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Anti-replay guard. Set to <c>true</c> on first consumption.
    /// A second consumption attempt is treated as a replay attack and denied.
    /// </summary>
    public bool IsUsed { get; set; }
}
