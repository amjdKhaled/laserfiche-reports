namespace LaserficheReports.Infrastructure.OAuth;

/// <summary>
/// Server-side store for OAuth2 authorization flow state entries.
/// Entries are keyed by a cryptographically random state string and expire
/// after ten minutes to limit the CSRF attack window.
/// </summary>
/// <remarks>
/// <para>
/// The state string is generated in <c>LoginController.StartSso</c>,
/// stored here and also written to the ASP.NET Core session.  The callback
/// validates that the <c>state</c> parameter in the LFDS redirect matches
/// the session value before calling <see cref="TryConsume"/>.
/// </para>
/// <para>
/// <see cref="TryConsume"/> is atomic with respect to replay: it marks an entry
/// as used and removes it in one step so a concurrent second request for the
/// same state string always receives <c>null</c>.
/// </para>
/// </remarks>
public interface IOAuthStateStore
{
    /// <summary>
    /// Stores a new state entry under the given <paramref name="state"/> key.
    /// Any existing entry for the same key is replaced.
    /// </summary>
    /// <param name="state">Cryptographically random state string (never null or empty).</param>
    /// <param name="entry">Entry to store.</param>
    void Store(string state, OAuthStateEntry entry);

    /// <summary>
    /// Retrieves and removes the state entry for <paramref name="state"/>,
    /// marking it as used to prevent replay.
    /// </summary>
    /// <returns>
    /// The entry when found, not expired, and not yet used;
    /// <c>null</c> in all other cases (unknown key, expired, replay attempt).
    /// </returns>
    OAuthStateEntry? TryConsume(string state);
}
