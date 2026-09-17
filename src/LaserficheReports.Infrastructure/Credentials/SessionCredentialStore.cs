using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Credentials;

/// <summary>
/// Stores per-session Laserfiche credentials in the ASP.NET Core session using
/// Data Protection to encrypt the password before it enters session state.
/// </summary>
/// <remarks>
/// <para>
/// Session keys used:
/// <list type="bullet">
///   <item><c>SessionCredUsername</c> — plain-text username (not sensitive).</item>
///   <item><c>SessionCredPasswordProtected</c> — Data Protection–encrypted password bytes
///     encoded as base-64 by the protector. Never stored in plain text.</item>
/// </list>
/// </para>
/// <para>
/// Registered as a singleton. The implementation is safe for singleton lifetime because
/// it reads <see cref="IHttpContextAccessor"/> at call time rather than capturing
/// <c>HttpContext</c> at construction time. When no HTTP context is present (health checks,
/// background tasks) <see cref="TryGetAsync"/> returns <c>null</c> and
/// <see cref="StoreAsync"/> / <see cref="ClearAsync"/> are no-ops.
/// </para>
/// </remarks>
internal sealed class SessionCredentialStore : ISessionCredentialStore
{
    /// <summary>Session key for the plain-text username.</summary>
    internal const string SessionKeyUsername = "SessionCredUsername";

    /// <summary>Session key for the Data Protection–encrypted password.</summary>
    internal const string SessionKeyPasswordProtected = "SessionCredPasswordProtected";

    private const string ProtectorPurpose = "LaserficheReports.SessionCredentials.v1";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDataProtector _protector;
    private readonly ILogger<SessionCredentialStore> _logger;

    /// <summary>Initialises the store.</summary>
    public SessionCredentialStore(
        IHttpContextAccessor httpContextAccessor,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<SessionCredentialStore> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _protector           = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _logger              = logger;
    }

    /// <inheritdoc />
    public Task StoreAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        if (session is null)
        {
            _logger.LogWarning("SessionCredentialStore.StoreAsync: no HTTP session available; credentials not stored.");
            return Task.CompletedTask;
        }

        session.SetString(SessionKeyUsername, username);
        session.SetString(SessionKeyPasswordProtected, _protector.Protect(password));

        _logger.LogDebug("Session credentials stored for user {Username}.", username);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LaserficheCredential?> TryGetAsync(
        CancellationToken cancellationToken = default)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        if (session is null)
            return Task.FromResult<LaserficheCredential?>(null);

        var username          = session.GetString(SessionKeyUsername);
        var protectedPassword = session.GetString(SessionKeyPasswordProtected);

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(protectedPassword))
            return Task.FromResult<LaserficheCredential?>(null);

        string password;
        try
        {
            password = _protector.Unprotect(protectedPassword);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionCredentialStore: failed to unprotect session password. Clearing stale entry.");
            session.Remove(SessionKeyUsername);
            session.Remove(SessionKeyPasswordProtected);
            return Task.FromResult<LaserficheCredential?>(null);
        }

        _logger.LogDebug("Session credentials retrieved for user {Username}.", username);
        return Task.FromResult<LaserficheCredential?>(new LaserficheCredential(username, password));
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        if (session is not null)
        {
            session.Remove(SessionKeyUsername);
            session.Remove(SessionKeyPasswordProtected);
            _logger.LogDebug("Session credentials cleared.");
        }
        return Task.CompletedTask;
    }
}
