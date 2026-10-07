using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Web;

internal static class ReportSessionEndpoints
{
    internal static bool ValidRepository(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 && !value.Any(char.IsControl);

    internal static void MapReportSessions(this WebApplication app)
    {
        app.MapGet("/api/session/status", async (ISessionCredentialStore sessions, IRepositoryContext repositories,
            HttpContext http, CancellationToken ct) =>
        {
            var credential = await sessions.TryGetAsync(ct);
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            return Results.Ok(new { authenticated = credential is not null, username = credential?.Username,
                repository = repository.RepositoryId, server = repository.ServerUrl, generation = http.Session.GetString("ReportsGeneration") });
        });
        app.MapPost("/api/session/repositories", async (LoginRequest request, IRepositoryContext context,
            ILaserficheRepositoryService service, CancellationToken ct) =>
        {
            var current = await context.GetActiveRepositoryAsync(ct);
            var id = request.RepositoryId?.Trim() ?? current.RepositoryId;
            if (!ValidRepository(id) || string.IsNullOrWhiteSpace(request.Username) || request.Password is null)
                return Results.BadRequest(new { error = "أدخل المستودع وبيانات الدخول لاكتشاف المستودعات." });
            var found = await service.DiscoverRepositoriesAsync(current.ServerUrl, id, request.Username, request.Password, ct);
            return Results.Ok(found.Select(r => new { id = r.RepositoryId, name = r.RepositoryName }));
        });
        app.MapPost("/api/session/login", LoginAsync);
        app.MapPost("/api/session/logout", async (ISessionCredentialStore sessions, ILaserficheAuthService auth,
            HttpContext http, CancellationToken ct) =>
        {
            await auth.InvalidateCurrentSessionTokensAsync();
            await sessions.ClearAsync(ct);
            http.Session.SetString("ReportsGeneration", Guid.NewGuid().ToString("N"));
            http.Session.Remove("AuthenticationScopeMethod");
            http.Session.Remove("AuthenticationScopeSubject");
            return Results.Ok(new { authenticated = false });
        });
    }
    internal static async Task<IResult> LoginAsync(LoginRequest request, IRepositoryContext repositories,
        ILaserficheAuthService auth, ISessionCredentialStore sessions, HttpContext http, CancellationToken ct)
    {
        var current = await repositories.GetActiveRepositoryAsync(ct);
        var id = request.RepositoryId?.Trim() ?? current.RepositoryId;
        if (!ValidRepository(id) || string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 256 || request.Password is null)
            return Results.BadRequest(new { error = "أدخل المستودع واسم المستخدم وكلمة المرور." });
        var username = request.Username.Trim();
        var previousCredential = await sessions.TryGetAsync(ct);
        if (previousCredential is not null &&
            string.Equals(current.RepositoryId, id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(previousCredential.Username, username, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(previousCredential.Password, request.Password, StringComparison.Ordinal))
        {
            try
            {
                await auth.GetTokenAsync(current, ct);
                return SignedIn(current, username, http);
            }
            catch (UnauthorizedAccessException) { /* Explicit sign-in can replace an expired session. */ }
        }

        // Authenticate in a provisional scope. A failed account/repository switch must
        // leave the previous credentials, token cache and repository selection intact.
        string[] keys = ["ActiveRepositoryId", "ReportsGeneration", "AuthenticationScopeMethod", "AuthenticationScopeSubject"];
        var previous = keys.ToDictionary(key => key, key => http.Session.GetString(key));
        http.Session.SetString("ActiveRepositoryId", id);
        http.Session.SetString("AuthenticationScopeMethod", "Reports");
        http.Session.SetString("AuthenticationScopeSubject", Guid.NewGuid().ToString("N"));
        var committed = false;
        try
        {
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            if (!await auth.TryAuthenticateAsync(repository, username, request.Password, ct))
                return Results.Unauthorized();
            await sessions.StoreAsync(username, request.Password, ct);
            http.Session.SetString("ReportsGeneration", Guid.NewGuid().ToString("N"));
            committed = true;
            return SignedIn(repository, username, http);
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

    private static IResult SignedIn(LaserficheReports.Application.DTOs.RepositoryDescriptor repository,
        string username, HttpContext http) => Results.Ok(new { authenticated = true, username,
            repository = repository.RepositoryId, server = repository.ServerUrl,
            generation = http.Session.GetString("ReportsGeneration") });

}
