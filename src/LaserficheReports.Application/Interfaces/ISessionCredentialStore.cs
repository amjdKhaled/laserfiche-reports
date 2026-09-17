using LaserficheReports.Domain.Common;

namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Stores and retrieves Laserfiche credentials for the current HTTP session,
/// protecting the password with ASP.NET Core Data Protection before placing it
/// in session state. Provides per-session credential isolation for the Desktop
/// Client login flow without writing credentials to disk.
/// </summary>
/// <remarks>
/// Credentials placed here are tied to the browser session and are cleared when
/// the user signs out, or when the session expires. They are never written to the
/// file system or placed in plain text anywhere.
/// </remarks>
public interface ISessionCredentialStore
{
    /// <summary>
    /// Encrypts <paramref name="password"/> with Data Protection and writes both
    /// values into the current ASP.NET Core session.
    /// </summary>
    /// <param name="username">Laserfiche username.</param>
    /// <param name="password">Plain-text password (may be empty).</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task StoreAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and decrypts the session credentials.
    /// Returns <c>null</c> when no session is available or no credentials have been stored.
    /// </summary>
    Task<LaserficheCredential?> TryGetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the stored credentials from the current session.
    /// Does nothing when no session is available.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
