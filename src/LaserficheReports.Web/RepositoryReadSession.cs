using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Web;

/// <summary>
/// Refuse repository reads unless this request belongs to a completed interactive
/// Reports or LFDS sign-in. A display name or a browser session cookie on its own
/// is not evidence of a repository identity.
/// </summary>
internal static class RepositoryReadSession
{
    internal static bool IsRepositoryReadPath(PathString path) =>
        path.StartsWithSegments("/api/reports") ||
        path.StartsWithSegments("/api/laserfiche") ||
        path.StartsWithSegments("/api/ingestion") ||
        path.StartsWithSegments("/api/recycle-bin") ||
        path.StartsWithSegments("/api/recycle") ||
        path.StartsWithSegments("/api/audit-trail");

    internal static async Task<bool> IsAuthorizedAsync(HttpContext context,
        RepositoryDescriptor repository, ISessionCredentialStore sessions, CancellationToken ct)
    {
        var session = context.Session;
        var method = session.GetString("AuthenticationScopeMethod");
        if (method is not ("Reports" or "LFDS") ||
            string.IsNullOrWhiteSpace(session.GetString("AuthenticationScopeSubject")) ||
            string.IsNullOrWhiteSpace(session.GetString("ReportsGeneration")) ||
            !string.Equals(session.GetString("ActiveRepositoryId"),
                repository.RepositoryId, StringComparison.OrdinalIgnoreCase))
            return false;

        var signedIn = await sessions.TryGetSignedInUserAsync(ct);
        if (string.IsNullOrWhiteSpace(signedIn)) return false;

        if (method == "Reports")
        {
            var credential = await sessions.TryGetAsync(ct);
            return credential is not null &&
                string.Equals(signedIn, credential.Username, StringComparison.OrdinalIgnoreCase);
        }

        // LFDS callback must have bound a real repository-token identity. The
        // legacy "LFDS" display placeholder is not an authenticated principal.
        var authenticated = session.GetString("AuthenticatedLaserficheUser");
        var ssoUser = session.GetString("LfdsUser");
        return !string.IsNullOrWhiteSpace(authenticated) &&
            !string.Equals(ssoUser, "LFDS", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(authenticated, ssoUser, StringComparison.OrdinalIgnoreCase);
    }
}
