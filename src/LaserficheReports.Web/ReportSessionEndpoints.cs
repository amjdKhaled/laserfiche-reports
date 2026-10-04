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
        app.MapPost("/api/session/login", async (LoginRequest request, IRepositoryContext repositories,
            ILaserficheAuthService auth, ISessionCredentialStore sessions, HttpContext http, CancellationToken ct) =>
        {
            var current = await repositories.GetActiveRepositoryAsync(ct);
            var id = request.RepositoryId?.Trim() ?? current.RepositoryId;
            if (!ValidRepository(id) || string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 256 || request.Password is null)
                return Results.BadRequest(new { error = "أدخل المستودع واسم المستخدم وكلمة المرور." });
            await auth.InvalidateCurrentSessionTokensAsync();
            await sessions.ClearAsync(ct);
            http.Session.SetString("ReportsGeneration", Guid.NewGuid().ToString("N"));
            http.Session.SetString("ActiveRepositoryId", id);
            http.Session.SetString("AuthenticationScopeMethod", "Reports");
            http.Session.SetString("AuthenticationScopeSubject", http.Session.Id);
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            if (!await auth.TryAuthenticateAsync(repository, request.Username, request.Password, ct))
            {
                await auth.InvalidateCurrentSessionTokensAsync();
                http.Session.Remove("AuthenticationScopeMethod");
                http.Session.Remove("AuthenticationScopeSubject");
                return Results.Unauthorized();
            }
            await sessions.StoreAsync(request.Username.Trim(), request.Password, ct);
            return Results.Ok(new { authenticated = true, username = request.Username.Trim(),
                repository = repository.RepositoryId, server = repository.ServerUrl, generation = http.Session.GetString("ReportsGeneration") });
        });
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
}
