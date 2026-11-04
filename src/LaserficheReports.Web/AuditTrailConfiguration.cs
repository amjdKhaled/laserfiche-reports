using System.Text.Json;
using System.Text.Json.Nodes;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Configuration;

namespace LaserficheReports.Web;

internal sealed record AuditSetupRequest(bool Enabled, string BaseUrl, AuditTrailReportOptions Report);

/// <summary>Merge only the selected repository's audit binding; preserve other-machine settings.</summary>
internal sealed class AuditTrailConfiguration(IHostEnvironment environment, IConfiguration configuration)
{
    internal async Task SaveAsync(string repositoryId, AuditSetupRequest request, CancellationToken ct)
    {
        var path = ReportsConfigPaths.ResolveWritableRuntimeConfigPath(environment.ContentRootPath);
        await SaveFileAsync(path, repositoryId, request, ct);
        if (configuration is IConfigurationRoot live) live.Reload();
        if (configuration.GetValue<bool>("AuditTrail:Enabled") != request.Enabled ||
            !string.Equals(configuration["AuditTrail:BaseUrl"]?.TrimEnd('/'), request.BaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("حُفظت الإعدادات، لكن ملف إعدادات محليًا أو متغير بيئة يتجاوزها. حدّث قسم AuditTrail فيه ثم افحص الاتصال.");
    }
    internal static async Task SaveFileAsync(string path, string repositoryId, AuditSetupRequest request, CancellationToken ct)
    {
        Validate(request);
        await ReportsConfigPaths.WriteLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var root = File.Exists(path)
                ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject
                    ?? throw new ArgumentException("ملف الإعدادات الحالي غير صالح؛ لم يُستبدل.")
                : new JsonObject();
            var audit = root["AuditTrail"] as JsonObject ?? new JsonObject();
            if (root["AuditTrail"] == null) root["AuditTrail"] = audit;
            var bindings = audit["Repositories"] as JsonObject ?? new JsonObject();
            if (audit["Repositories"] == null) audit["Repositories"] = bindings;
            audit["Enabled"] = request.Enabled;
            audit["BaseUrl"] = request.BaseUrl.Trim().TrimEnd('/');
            audit["TimeoutSeconds"] = 3600;
            var key = bindings.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, repositoryId, StringComparison.OrdinalIgnoreCase)) ?? repositoryId;
            bindings[key] = JsonSerializer.SerializeToNode(request.Report);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (JsonException) { throw new ArgumentException("ملف الإعدادات الحالي غير صالح؛ لم يُستبدل."); }
        finally { ReportsConfigPaths.WriteLock.Release(); }
    }
    internal static void Validate(AuditSetupRequest request)
    {
        if (request.BaseUrl == null || request.Report == null) throw new ArgumentException("إعدادات سجل التدقيق ناقصة.");
        if (!request.Enabled) return;
        if (!Uri.TryCreate(request.BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("أدخل عنوان HTTPS لموقع Audit Trail، أو HTTP على الجهاز نفسه.");
        var report = request.Report;
        if (string.IsNullOrWhiteSpace(report.DataSource) || string.IsNullOrWhiteSpace(report.ReportId) ||
            string.IsNullOrWhiteSpace(report.ScopeDescription) || string.IsNullOrWhiteSpace(report.ExportDirectory) ||
            !Path.IsPathFullyQualified(report.ExportDirectory))
            throw new ArgumentException("أكمل مصدر البيانات ومعرّف التقرير ومسار التصدير الكامل ووصف نطاقه.");
        string[] allowed = ["eventTime", "eventType", "userName", "entryId", "entryName", "outcome"];
        if (report.Columns == null || report.Columns.Keys.Except(allowed).Any() ||
            new[] { "eventTime", "eventType", "userName" }.Except(report.Columns.Keys).Any() ||
            report.Columns.Values.Any(string.IsNullOrWhiteSpace) || report.Columns.Values.Distinct().Count() != report.Columns.Count)
            throw new ArgumentException("حدد أسماء أعمدة وقت الحدث ونوعه والمستخدم كما تظهر في CSV الفعلي، دون تكرار.");
        if (report.EventLabels is null || report.EventLabels.Count > 200 || report.EventLabels.Any(p =>
            string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Value) || p.Key.Length > 256 || p.Value.Length > 256))
            throw new ArgumentException("خريطة أسماء الأحداث غير صالحة؛ الحد 200 نوع حدث.");
    }
}

internal static class AuditTrailEndpoints
{
    internal static void MapAuditTrailSetup(this WebApplication app)
    {
        app.MapGet("/api/audit-trail/settings", async (AuditTrailSource source, IRepositoryContext repositories,
            ISessionCredentialStore sessions, AuditSessionCredentials credentials, CancellationToken ct) =>
        {
            if (await sessions.TryGetAsync(ct) == null) return Results.Unauthorized();
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            var options = source.Options;
            var report = options.Repositories.FirstOrDefault(p => string.Equals(p.Key, repository.RepositoryId, StringComparison.OrdinalIgnoreCase)).Value;
            return Results.Ok(new { options.Enabled, options.BaseUrl, repository = repository.RepositoryId,
                report = report ?? new AuditTrailReportOptions(), windowsUsername = credentials.Read()?.Username,
                configured = options.Enabled && report != null });
        });
        app.MapPost("/api/audit-trail/settings", async (AuditSetupRequest request, AuditTrailConfiguration settings,
            IRepositoryContext repositories, ISessionCredentialStore sessions, CancellationToken ct) =>
        {
            if (await sessions.TryGetAsync(ct) == null) return Results.Unauthorized();
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            await settings.SaveAsync(repository.RepositoryId, request, ct);
            return Results.Ok(new { saved = true });
        });
        app.MapPost("/api/audit-trail/credentials", async (AuditWindowsLogin request,
            AuditSessionCredentials credentials, ISessionCredentialStore sessions, CancellationToken ct) =>
        {
            if (await sessions.TryGetAsync(ct) == null) return Results.Unauthorized();
            if (request.Username is null || request.Password is null) return Results.BadRequest();
            credentials.Store(request.Username, request.Password);
            return Results.Ok(new { saved = true });
        });
        app.MapPost("/api/audit-trail/test", async (AuditTrailSource source, ISessionCredentialStore sessions, CancellationToken ct) =>
        {
            if (await sessions.TryGetAsync(ct) == null) return Results.Unauthorized();
            var snapshot = await source.ReadAsync(ct);
            return Results.Ok(new { status = "available", eventCount = snapshot.Events.Count,
                scope = snapshot.ScopeDescription, exportedAt = snapshot.ExportedAt });
        });
    }
    internal sealed record AuditWindowsLogin(string? Username, string? Password);
}
