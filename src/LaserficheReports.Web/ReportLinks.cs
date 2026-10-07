using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;

namespace LaserficheReports.Web;

internal static class ReportLinks
{
    internal static IReadOnlyList<string> Build(string baseUrl, string repository, IEnumerable<int> entries)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("اضبط Laserfiche:WebClientBaseUrl على عنوان Web Client مثل https://localhost/laserfiche.");
        var ids = entries.Where(id => id > 0).Distinct().ToArray();
        var prefix = baseUrl.TrimEnd('/') + "/Browse.aspx?db=" + Uri.EscapeDataString(repository) + "#?search=";
        // Bounded groups keep large inventories usable without opening hundreds of tabs.
        return ids.Chunk(500).Select(group => prefix + Uri.EscapeDataString(
            string.Join(" | ", group.Select(id => "{LF:ID=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}")))).ToArray();
    }

    internal static async Task<bool> ValidateAsync(ILaserficheSearchService searches, int[] ids, CancellationToken ct)
    {
        // Revalidate access live in bounded search batches instead of one GET per ID.
        foreach (var group in ids.Chunk(100))
        {
            var expression = "{LF:Name=\"*\", Type=DF} & (" + string.Join(" | ", group.Select(id => $"{{LF:ID={id}}}")) + ")";
            var live = await searches.QueryAsync(expression, 1, 100, readAll: true, cancellationToken: ct);
            if (live.HasNextPage || !live.IsTotalCountExact || !group.ToHashSet().SetEquals(live.Items.Select(i => i.EntryId))) return false;
        }
        return true;
    }

    internal static void MapReportLinks(this WebApplication app)
    {
        app.MapPost("/api/reports/laserfiche-links", async (ReportLinkRequest request, IRepositoryContext repositories,
            ISessionCredentialStore sessions, ILaserficheSearchService searches, IConfiguration config, CancellationToken ct) =>
        {
            if (await sessions.TryGetAsync(ct) is null) return Results.Unauthorized();
            var repository = await repositories.GetActiveRepositoryAsync(ct);
            if (!string.Equals(request.RepositoryId, repository.RepositoryId, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "افتح المستودع الخاص بالتقرير أولًا." });
            if (request.EntryIds is null || request.EntryIds.Length > 20000 || request.EntryIds.Any(id => id <= 0))
                return Results.BadRequest(new { error = "قائمة وثائق التقرير غير صالحة." });
            var ids = request.EntryIds.Distinct().ToArray();
            if (!await ValidateAsync(searches, ids, ct))
                return Results.BadRequest(new { error = "أحد عناصر التقرير لم يعد متاحًا في المستودع الحالي." });
            var baseUrl = config["Laserfiche:WebClientBaseUrl"] ?? new Uri(repository.ServerUrl).GetLeftPart(UriPartial.Authority) + "/laserfiche";
            return Results.Ok(new { urls = Build(baseUrl, repository.RepositoryId, ids), documentCount = ids.Length });
        });
    }
}
internal sealed record ReportLinkRequest(string RepositoryId, int[] EntryIds);
