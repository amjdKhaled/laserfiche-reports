using System.Text.Json;
using System.Text.Json.Nodes;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Configuration;

namespace LaserficheReports.Web;
internal sealed class RecycleBinConfiguration(IHostEnvironment environment, IConfiguration configuration)
{
    internal async Task SaveAsync(string repositoryId, RecycleBinBinding binding, CancellationToken ct)
    {
        await SaveFileAsync(ReportsConfigPaths.ResolveWritableRuntimeConfigPath(environment.ContentRootPath), repositoryId, binding, ct);
        if (configuration is IConfigurationRoot root) root.Reload();
        var effective = configuration.GetSection("RecycleBin:Repositories").GetChildren()
            .FirstOrDefault(c => string.Equals(c.Key, repositoryId, StringComparison.OrdinalIgnoreCase))?.Get<RecycleBinBinding>();
        if (effective == null || effective.Enabled != binding.Enabled || effective.Secure != binding.Secure ||
            effective.ServerName != binding.ServerName || effective.RepositoryName != binding.RepositoryName)
            throw new ArgumentException("حُفظ الربط لكن ملفًا محليًا أو متغير بيئة يتجاوز قيمه؛ حدّث قسم RecycleBin فيه ثم افحص الاتصال.");
    }
    internal static async Task SaveFileAsync(string path, string repositoryId, RecycleBinBinding binding, CancellationToken ct)
    {
        if (new[] { binding.ServerName, binding.RepositoryName }.Any(v => v == null || v.Length > 256 || v.Any(char.IsControl)) ||
            binding.Enabled && (string.IsNullOrWhiteSpace(binding.ServerName) || string.IsNullOrWhiteSpace(binding.RepositoryName) || binding.ServerName.Contains("://")))
            throw new ArgumentException("أدخل اسم Laserfiche Server واسم المستودع الفعليين، دون عنوان API.");
        await ReportsConfigPaths.WriteLock.WaitAsync(ct);
        try
        {
            var root = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject
                ?? throw new ArgumentException("ملف الإعدادات غير صالح؛ لم يُستبدل.") : new JsonObject();
            if (root["RecycleBin"] == null) root["RecycleBin"] = new JsonObject();
            var section = root["RecycleBin"] as JsonObject ?? throw new ArgumentException("إعدادات السلة غير صالحة.");
            if (section["Repositories"] == null) section["Repositories"] = new JsonObject();
            var repositories = section["Repositories"] as JsonObject ?? throw new ArgumentException("إعدادات مستودعات السلة غير صالحة.");
            var key = repositories.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, repositoryId, StringComparison.OrdinalIgnoreCase)) ?? repositoryId;
            binding.ServerName = binding.ServerName.Trim(); binding.RepositoryName = binding.RepositoryName.Trim();
            repositories[key] = JsonSerializer.SerializeToNode(binding);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (JsonException) { throw new ArgumentException("ملف الإعدادات غير صالح؛ لم يُستبدل."); }
        finally { ReportsConfigPaths.WriteLock.Release(); }
    }
}
internal static class RecycleBinEndpoints
{
    internal static void MapRecycleBin(this WebApplication app)
    {
        app.MapGet("/api/recycle-bin/settings", async (RecycleBinSource source, IRepositoryContext repositories, ISessionCredentialStore sessions, CancellationToken ct) => {
            if (await sessions.TryGetAsync(ct) == null) return Results.Unauthorized();
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            return Results.Ok(new { binding = source.Binding(repository.RepositoryId) ?? new RecycleBinBinding(), reason = source.UnavailableReason(repository.RepositoryId) });
        });
        app.MapPost("/api/recycle-bin/settings", async (RecycleBinBinding binding, RecycleBinConfiguration settings, IRepositoryContext repositories, ISessionCredentialStore sessions, CancellationToken ct) => {
            if (await sessions.TryGetAsync(ct) == null) return Results.Unauthorized();
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            await settings.SaveAsync(repository.RepositoryId, binding, ct);
            return Results.Ok(new { saved = true });
        });
        app.MapGet("/api/reports/recycle-bin", async (RecycleBinSource source, IRepositoryContext repositories, ISessionCredentialStore sessions, bool? countOnly, string? entryType, int? page, int? limit, CancellationToken ct) => {
            if (await sessions.TryGetAsync(ct) == null) return Results.Unauthorized();
            var plan = new QueryPlan(Operation: "recycle_bin", Title: "سلة المحذوفات", EntryType: entryType ?? "document", Page: page ?? 1, Limit: limit ?? 50, CountOnly: countOnly ?? false);
            RecycleBinReport.Validate(plan);
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            return Results.Ok(RecycleBinReport.Build(await source.ReadAsync(ct), repository.RepositoryId, plan));
        });
    }
}
