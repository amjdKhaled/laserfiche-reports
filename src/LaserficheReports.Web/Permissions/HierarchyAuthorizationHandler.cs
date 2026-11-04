using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Web.Permissions;

// Registered ONLY on LaserficheAuthenticated; it also runs on bearer-handler retries.
// Handler lifetimes differ from request lifetimes; resolve the gateway from the CURRENT request.
internal sealed class HierarchyAuthorizationHandler(IHttpContextAccessor contexts) : DelegatingHandler
{
    private static readonly Regex EntryRoute = new(@"/Repositories/([^/]+)/Entries/(\d+)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var context = contexts.HttpContext ?? throw Unavailable();
        var gateway = context.RequestServices.GetRequiredService<IFullHierarchyAuthorizationService>();
        var repository = await context.RequestServices.GetRequiredService<IRepositoryContext>().GetActiveRepositoryAsync(ct);
        var uri = request.RequestUri ?? throw Unavailable();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var repoIndex = Array.FindIndex(segments, s => s.Equals("Repositories", StringComparison.OrdinalIgnoreCase));
        if (!Uri.TryCreate(repository.ServerUrl, UriKind.Absolute, out var apiHost) || apiHost.Scheme != uri.Scheme || apiHost.Authority != uri.Authority)
            throw Unavailable();
        // Repository discovery carries connection metadata, not entry rows. Existing sign-in/session
        // guards and the bearer handler still apply. It has no repository-id path segment.
        if (repoIndex == segments.Length - 1) return await base.SendAsync(request, ct);
        if (repoIndex < 0 || repoIndex + 1 >= segments.Length ||
            !string.Equals(Uri.UnescapeDataString(segments[repoIndex + 1]), repository.RepositoryId, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(repository.ServerUrl, UriKind.Absolute, out var host) || host.Scheme != uri.Scheme || host.Authority != uri.Authority)
            throw Unavailable();
        var entry = EntryRoute.Match(uri.AbsolutePath);
        if (entry.Success && (!int.TryParse(entry.Groups[2].Value, out var id) || !await gateway.CanReadAsync(id, ct)))
            throw new PermissionVerificationException(new(PermissionStatus.AncestorAccessDenied));
        var response = await base.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return response; // Actual operation 401/403 remains decisive.
        try
        {
            var path = uri.AbsolutePath;
            var candidateList = path.Contains("/Searches", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/SimpleSearches", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/Children", StringComparison.OrdinalIgnoreCase);
            var byPath = path.EndsWith("/Entries/ByPath", StringComparison.OrdinalIgnoreCase);
            if (!candidateList && !byPath)
            {
                if (entry.Success)
                {
                    var binary = (path.EndsWith("/Edoc", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/Export", StringComparison.OrdinalIgnoreCase)) &&
                        response.Content.Headers.ContentLength is null or > 33554432;
                    await context.RequestServices.GetRequiredService<ProtectedReadTracker>().RecordAsync(uri, response, !binary, ct);
                }
                return response;
            }
            if (response.Content.Headers.ContentLength > 33554432) throw Unavailable();
            await response.Content.LoadIntoBufferAsync(33554432);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw Unavailable();
            if (byPath)
            {
                var node = json["entry"] ?? json;
                var resolvedId = ReadId(node);
                if (!await gateway.CanReadAsync(resolvedId, ct)) throw new PermissionVerificationException(new(PermissionStatus.AncestorAccessDenied));
                return response;
            }
            var rows = json as JsonArray ?? (json as JsonObject)?["value"] as JsonArray;
            if (rows == null)
            {
                // Search submission/status envelopes do not contain entries.
                if (path.Contains("Search", StringComparison.OrdinalIgnoreCase)) return response;
                throw Unavailable();
            }
            if (rows.Count > 10000) throw Unavailable();
            if (json is JsonObject candidateEnvelope && candidateEnvelope["@odata.nextLink"] == null && candidateEnvelope["nextLink"] == null)
                foreach (var key in new[] { "@odata.count", "totalCount", "count" })
                    if (candidateEnvelope[key] is JsonValue count && count.TryGetValue<int>(out var expected) && expected > rows.Count)
                        throw Unavailable(); // Missing candidate rows cannot become an exact authorized zero/count.
            var ids = rows.Select(row => ReadId(row ?? throw Unavailable())).Distinct().ToArray();
            var allowed = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (id, token) => allowed[id] = await gateway.CanReadAsync(id, token));
            for (var i = rows.Count - 1; i >= 0; i--) if (!allowed[ReadId(rows[i]!)]) rows.RemoveAt(i);
            // Projected field values also require a fresh field-operation dependency for saved/exported reports.
            foreach (var row in rows)
                if (row?["fields"] is JsonArray { Count: > 0 })
                    await context.RequestServices.GetRequiredService<ILaserficheEntryService>().GetEntryFieldsAsync(ReadId(row), ct);
            if (json is JsonObject envelope)
                foreach (var key in new[] { "@odata.count", "totalCount", "count" }) envelope.Remove(key);
            var old = response.Content;
            response.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
            old.Dispose();
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    private static int ReadId(JsonNode node) => node["id"] is JsonValue value && value.TryGetValue<int>(out var id) && id > 0 ? id : throw Unavailable();
    private static PermissionVerificationException Unavailable() => new(new(PermissionStatus.VerificationUnavailable));
}
