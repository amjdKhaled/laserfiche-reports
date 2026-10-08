using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

internal sealed class LaserficheTagDefinitionService(IHttpClientFactory clients,
    IRepositoryContext repositories, ILaserficheApiAdapter adapter,
    ILogger<LaserficheTagDefinitionService> logger) : ILaserficheTagDefinitionService
{
    public async Task<IReadOnlyList<LFTagDefinition>> GetTagDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        var repository = await repositories.GetActiveRepositoryAsync(cancellationToken);
        using var client = clients.CreateClient("LaserficheAuthenticated");
        var first = new Uri(adapter.BuildTagDefinitionsUrl(repository.RepositoryId), UriKind.Absolute);
        string? next = first.AbsoluteUri;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<int, LFTagDefinition>();
        while (next is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(next)) throw new JsonException("TagDefinitions repeated a continuation link.");
            using var response = await RepositoryDefinitionReader.GetAsync(client, next, logger, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LaserficheException("Unable to read current repository tag definitions.", (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root : Property(root, "value");
            if (items.ValueKind != JsonValueKind.Array) throw new JsonException("TagDefinitions did not return an array.");
            foreach (var item in items.EnumerateArray())
            {
                var id = Property(item, "id");
                var name = Property(item, "name");
                if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number <= 0 ||
                    name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                    throw new JsonException("A tag definition is missing its ID or name.");
                var description = Property(item, "description");
                result[number] = new(number, name.GetString()!, description.ValueKind == JsonValueKind.String ? description.GetString() : null);
            }
            var continuation = Property(root, "@odata.nextLink");
            if (continuation.ValueKind == JsonValueKind.Undefined) continuation = Property(root, "nextLink");
            if (continuation.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { next = null; continue; }
            if (continuation.ValueKind != JsonValueKind.String) throw new JsonException("Invalid tag continuation link.");
            var link = continuation.GetString();
            if (string.IsNullOrWhiteSpace(link)) { next = null; continue; }
            var resolved = new Uri(new Uri(next), link);
            if (resolved.Scheme != first.Scheme || resolved.Authority != first.Authority ||
                resolved.AbsolutePath != first.AbsolutePath)
                throw new JsonException("TagDefinitions continuation left the active collection.");
            next = resolved.AbsoluteUri;
        }
        logger.LogInformation("Stage=TAG_CATALOG Definitions={Definitions} Pages={Pages}", result.Count, visited.Count);
        return result.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    }

    private static JsonElement Property(JsonElement source, string name) => source.ValueKind == JsonValueKind.Object
        ? source.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
        : default;
}
