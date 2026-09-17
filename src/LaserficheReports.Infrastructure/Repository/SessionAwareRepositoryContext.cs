using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Repository;

/// <summary>
/// Resolves the active repository from the current HTTP session when present,
/// falling back to the live <see cref="LaserficheOptions"/> for direct-browser access.
/// </summary>
/// <remarks>
/// <para>
/// When the Laserfiche Desktop Client opens the portal via a button click, the extension
/// appends <c>?repository=&lt;DatabaseName&gt;</c> to the portal URL.
/// <see cref="RepositorySessionMiddleware"/> intercepts this parameter and stores it in
/// the ASP.NET Core session under the key <c>ActiveRepositoryId</c> so that all
/// subsequent navigation within the same browser session uses the Desktop Client's
/// active repository rather than the configured default.
/// </para>
/// <para>
/// When a user opens the portal directly in a browser (no Desktop Client context), no
/// session override is present and the configured <see cref="LaserficheOptions.RepositoryId"/>
/// is used as the fallback, preserving the original single-repository behaviour.
/// </para>
/// <para>
/// Registered as a singleton. Safe for singleton-scoped services such as
/// <c>BearerTokenHandler</c> because it reads the live <see cref="IHttpContextAccessor"/>
/// on every call rather than capturing any request-scoped state at construction time.
/// </para>
/// </remarks>
internal sealed class SessionAwareRepositoryContext : IRepositoryContext
{
    /// <summary>Session key that stores the Desktop Client–provided repository identifier.</summary>
    internal const string SessionKeyRepositoryId = "ActiveRepositoryId";

    /// <summary>Session key that stores the human-readable source label for UI display.</summary>
    internal const string SessionKeyRepositorySource = "ActiveRepositorySource";

    private readonly IOptionsMonitor<LaserficheOptions> _optionsMonitor;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Initialises the context with live options and an HTTP-context accessor.</summary>
    public SessionAwareRepositoryContext(
        IOptionsMonitor<LaserficheOptions> optionsMonitor,
        IHttpContextAccessor httpContextAccessor)
    {
        _optionsMonitor      = optionsMonitor;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the session-scoped repository when the user arrived via the Desktop Client;
    /// returns the configured default otherwise.
    /// </remarks>
    public Task<RepositoryDescriptor> GetActiveRepositoryAsync(
        CancellationToken cancellationToken = default)
    {
        var opt = _optionsMonitor.CurrentValue;

        // Attempt to read the session-stored repository ID.
        // The try/catch guards against contexts where IHttpContextAccessor.HttpContext
        // is null (background tasks, startup, health checks).
        string? sessionRepoId = null;
        try
        {
            sessionRepoId = _httpContextAccessor.HttpContext?
                .Session.GetString(SessionKeyRepositoryId);
        }
        catch
        {
            // Session not available in this context — fall through to config fallback.
        }

        var hasSessionRepo = !string.IsNullOrWhiteSpace(sessionRepoId);
        var repoId = hasSessionRepo ? sessionRepoId! : opt.RepositoryId;

        // When the repository came from the session (Desktop/Web Client launch
        // or login-page selection), the configured display name refers to a
        // different repository — use the session repository id as the label.
        var displayName = hasSessionRepo ? repoId : opt.EffectiveDisplayName;

        var descriptor = new RepositoryDescriptor(
            // The credential/token key must be repository-specific. Keeping this as
        // "default" meant a selected repository could read the credentials saved
        // for a different repository when the session changed.
        Key:          repoId,
            ServerUrl:    opt.ServerUrl.TrimEnd('/'),
            RepositoryId: repoId,
            DisplayName:  displayName);

        return Task.FromResult(descriptor);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(
        CancellationToken cancellationToken = default) =>
        [await GetActiveRepositoryAsync(cancellationToken)];
}
