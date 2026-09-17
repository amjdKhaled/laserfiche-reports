using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Decorates the legacy entry implementation and provides source-complete hierarchy,
/// detail, and template mapping: dynamic root discovery, complete pagination,
/// de-duplication, and cross-version response aliases.
/// </summary>
internal sealed class CompleteLaserficheEntryService : ILaserficheEntryService
{
    private static readonly ConcurrentDictionary<string, int> RootIdCache =
        new(StringComparer.OrdinalIgnoreCase);

    // The legacy implementation is retained only for its robust entry-field parser.
    private readonly LaserficheEntryService _inner;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILaserficheApiAdapter _adapter;
    private readonly ILaserficheTemplateService _templateService;
    private readonly ILogger<CompleteLaserficheEntryService> _logger;

    public CompleteLaserficheEntryService(
        LaserficheEntryService inner,
        IHttpClientFactory httpClientFactory,
        IRepositoryContext repositoryContext,
        ILaserficheApiAdapter adapter,
        ILaserficheTemplateService templateService,
        ILogger<CompleteLaserficheEntryService> logger)
    {
        _inner = inner;
        _httpClientFactory = httpClientFactory;
        _repositoryContext = repositoryContext;
        _adapter = adapter;
        _templateService = templateService;
        _logger = logger;
    }

    public async Task<LFEntry> GetEntryAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext.GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);
        var url = _adapter.BuildEntryUrl(repo.RepositoryId, entryId, Adapters.EntryResource.Details);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LaserficheException(
                $"Laserfiche API returned HTTP {(int)response.StatusCode} for {url}. Body: {body}",
                (int)response.StatusCode);
        }

        var resource = ParseSingleEntry(body)
            ?? throw new LaserficheException(
                $"Entry {entryId} response did not contain an entry object.",
                (int)response.StatusCode);

        return MapEntry(resource);
    }

    public Task<IReadOnlyList<LFFieldValue>> GetEntryFieldsAsync(
        int entryId,
        CancellationToken cancellationToken = default) =>
        _inner.GetEntryFieldsAsync(entryId, cancellationToken);

    public async Task<LFTemplate?> GetEntryTemplateAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false);
        var hasId = entry.TemplateId is > 0;
        var hasName = !string.IsNullOrWhiteSpace(entry.TemplateName);

        if (!hasId && !hasName)
            return null;

        LFTemplateDefinition? definition = null;
        if (!hasId || !hasName)
        {
            var definitions = await _templateService
                .GetTemplateDefinitionsAsync(cancellationToken)
                .ConfigureAwait(false);

            definition = hasId
                ? definitions.FirstOrDefault(t => t.Id == entry.TemplateId)
                : definitions.FirstOrDefault(t => string.Equals(
                    t.Name,
                    entry.TemplateName?.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        var templateId = hasId ? entry.TemplateId!.Value : definition?.Id ?? 0;
        var templateName = hasName ? entry.TemplateName!.Trim() : definition?.Name ?? string.Empty;

        if (templateId <= 0 || string.IsNullOrWhiteSpace(templateName))
        {
            _logger.LogWarning(
                "Entry {EntryId} appears templated but the template could not be resolved authoritatively. TemplateId={TemplateId}; TemplateName={TemplateName}.",
                entryId,
                entry.TemplateId,
                entry.TemplateName ?? "(none)");
            return null;
        }

        var fields = await GetEntryFieldsAsync(entryId, cancellationToken).ConfigureAwait(false);

        return new LFTemplate
        {
            Id = templateId,
            Name = templateName,
            Description = definition?.Description,
            Fields = fields.Select(f => new LFFieldDefinition
            {
                Name = f.FieldName,
                FieldType = f.FieldType ?? "String",
                IsRequired = f.IsRequired,
                IsMultiValue = f.IsMultiValue
            }).ToList().AsReadOnly()
        };
    }

    public async Task<string> GetEntryPathAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false);
        return entry.FullPath;
    }

    public async Task<int> GetRootEntryIdAsync(CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var cacheKey = $"{repo.ServerUrl.TrimEnd('/')}|{repo.RepositoryId}|{_adapter.ApiVersion}";
        if (RootIdCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var byPathUrl = _adapter.BuildEntryByPathUrl(repo.RepositoryId, @"\");
        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        try
        {
            using var response = await client.GetAsync(byPathUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var discovered = ParseRootId(body);
                if (discovered > 0)
                {
                    RootIdCache[cacheKey] = discovered;
                    _logger.LogInformation(
                        "Authoritative repository root discovered. Repository={RepositoryId}; RootEntryId={RootEntryId}.",
                        repo.RepositoryId, discovered);
                    return discovered;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Root ByPath request failed. Repository={RepositoryId}; HTTP={Status}; URL={Url}; Body={Body}",
                    repo.RepositoryId,
                    (int)response.StatusCode,
                    byPathUrl,
                    body.Length > 500 ? body[..500] + "…" : body);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Root ByPath discovery failed for repository {RepositoryId}.", repo.RepositoryId);
        }

        var configuredFallback = _adapter.GetConfiguredRootEntryId();
        if (configuredFallback > 0)
        {
            _logger.LogWarning(
                "Using explicitly configured RootEntryId={RootEntryId} because dynamic root discovery failed for repository {RepositoryId}.",
                configuredFallback, repo.RepositoryId);
            RootIdCache[cacheKey] = configuredFallback;
            return configuredFallback;
        }

        throw new LaserficheException(
            $"Could not discover the root entry for repository '{repo.RepositoryId}'. No explicit RootEntryId fallback is configured.",
            500);
    }

    public async Task<PagedResult<LFEntry>> GetEntryChildrenAsync(
        int entryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be at least 1.");
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be at least 1.");

        var allEntries = await GetAllFolderChildrenAsync(entryId, cancellationToken)
            .ConfigureAwait(false);
        var skip = (page - 1) * pageSize;

        return new PagedResult<LFEntry>
        {
            Items = allEntries.Skip(skip).Take(pageSize).ToList().AsReadOnly(),
            TotalCount = allEntries.Count,
            PageNumber = page,
            PageSize = pageSize
        };
    }

    public async Task<IReadOnlyList<LFEntry>> GetAllFolderChildrenAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var firstUrl = _adapter.BuildFolderChildrenUrl(repo.RepositoryId, entryId);
        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        var allEntries = new List<LFEntry>();
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextUrl = firstUrl;
        var pageNumber = 0;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!visitedUrls.Add(nextUrl))
            {
                throw new LaserficheException(
                    $"Folder children pagination repeated a nextLink for entry {entryId}: {nextUrl}",
                    500);
            }

            pageNumber++;
            using var response = await client.GetAsync(nextUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LaserficheException(
                    $"Laserfiche API returned HTTP {(int)response.StatusCode} while listing folder {entryId}, page {pageNumber}, URL {nextUrl}. Body: {body}",
                    (int)response.StatusCode);
            }

            var parsed = ParsePage(body);
            // A missing type must not silently erase a document from every KPI.
            // Ask the entry endpoint for authoritative details; never guess from a
            // file extension, page count, name, or the presence of a template.
            for (var i = 0; i < parsed.Entries.Count; i++)
            {
                if (parsed.Entries[i].Id <= 0)
                    throw new JsonException("Folder listing contained an invalid entry ID.");
                if (parsed.Entries[i].EntryType != LFEntryType.Unknown)
                    continue;
                var details = await GetEntryAsync(parsed.Entries[i].Id, cancellationToken)
                    .ConfigureAwait(false);
                if (details.Id != parsed.Entries[i].Id || details.EntryType == LFEntryType.Unknown)
                    throw new JsonException($"Cannot determine the type of entry {parsed.Entries[i].Id}; repository totals are incomplete.");
                parsed.Entries[i] = details;
            }
            allEntries.AddRange(parsed.Entries);
            nextUrl = ResolveNextLink(nextUrl, parsed.NextLink);

            _logger.LogInformation(
                "Complete folder listing. EntryId={EntryId}; Page={Page}; PageEntries={PageEntries}; RunningTotal={RunningTotal}; HasNext={HasNext}.",
                entryId, pageNumber, parsed.Entries.Count, allEntries.Count, nextUrl is not null);
        }

        var unique = allEntries
            .GroupBy(e => e.Id)
            .Select(g => g
                .OrderByDescending(e => e.LastModifiedTime ?? e.CreationTime ?? DateTimeOffset.MinValue)
                .First())
            .ToList()
            .AsReadOnly();

        _logger.LogInformation(
            "Complete folder listing finished. EntryId={EntryId}; Pages={Pages}; UniqueEntries={Count}.",
            entryId, pageNumber, unique.Count);

        return unique;
    }

    public async Task<IReadOnlyList<LFEntry>> GetFolderTreeAsync(
        int rootEntryId,
        int depth,
        CancellationToken cancellationToken = default)
    {
        if (depth is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(depth), "Folder tree depth must be between 1 and 5.");

        var result = new List<LFEntry>();
        var visited = new HashSet<int>();
        await TraverseFolderAsync(rootEntryId, depth, 0, result, visited, cancellationToken)
            .ConfigureAwait(false);
        return result.AsReadOnly();
    }

    private async Task TraverseFolderAsync(
        int folderId,
        int maxDepth,
        int currentDepth,
        List<LFEntry> accumulator,
        HashSet<int> visited,
        CancellationToken cancellationToken)
    {
        if (currentDepth >= maxDepth || !visited.Add(folderId))
            return;

        var children = await GetAllFolderChildrenAsync(folderId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var child in children.Where(e => e.EntryType == LFEntryType.Folder))
        {
            accumulator.Add(child);
            await TraverseFolderAsync(
                    child.Id,
                    maxDepth,
                    currentDepth + 1,
                    accumulator,
                    visited,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static EntryResource? ParseSingleEntry(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetPropertyIgnoreCase(root, "entry", out var wrapped) &&
            wrapped.ValueKind == JsonValueKind.Object)
        {
            return wrapped.Deserialize<EntryResource>(JsonOptions.Default);
        }

        return root.Deserialize<EntryResource>(JsonOptions.Default);
    }

    private static int ParseRootId(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return 0;

        if (TryReadPositiveId(root, out var directId))
            return directId;

        if (TryGetPropertyIgnoreCase(root, "entry", out var entry) &&
            entry.ValueKind == JsonValueKind.Object &&
            TryReadPositiveId(entry, out var wrappedId))
            return wrappedId;

        return 0;
    }

    private static bool TryReadPositiveId(JsonElement element, out int id)
    {
        id = 0;
        if (!TryGetPropertyIgnoreCase(element, "id", out var idElement))
            return false;

        if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out id))
            return id > 0;

        return int.TryParse(idElement.ToString(), out id) && id > 0;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static EntryPage ParsePage(string body)
    {
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new JsonException("Folder children response body was empty.");

        if (body.StartsWith('['))
        {
            var resources = JsonSerializer.Deserialize<List<EntryResource>>(body, JsonOptions.Default) ?? [];
            return new EntryPage(resources.Select(MapEntry).ToList(), null);
        }

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(json.RootElement, "value", out var values) ||
            values.ValueKind != JsonValueKind.Array)
            throw new JsonException("Folder children response did not contain a value array; refusing to report empty totals.");

        var envelope = JsonSerializer.Deserialize<ODataPagedList<EntryResource>>(body, JsonOptions.Default)
            ?? throw new JsonException("Folder children response could not be deserialized.");

        return new EntryPage(
            envelope.Value.Select(MapEntry).ToList(),
            envelope.NextLink ?? envelope.PlainNextLink);
    }

    private static string? ResolveNextLink(string currentUrl, string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink) ||
            !Uri.TryCreate(currentUrl, UriKind.Absolute, out var current))
            return null;

        if (!Uri.TryCreate(current, nextLink, out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(resolved.Scheme, current.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Authority, current.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException(
                $"Folder children nextLink points outside the active Laserfiche API host: {nextLink}");
        }

        return resolved.AbsoluteUri;
    }

    private static LFEntry MapEntry(EntryResource r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        ParentId = r.ParentId,
        FullPath = r.FullPath,
        FolderPath = r.FolderPath,
        Creator = r.Creator,
        CreationTime = r.CreationTime ?? r.CreatedTime,
        LastModifiedTime = r.LastModifiedTime ?? r.ModifiedTime,
        EntryType = ParseEntryType(r.EntryType) is var type && type != LFEntryType.Unknown
            ? type : ParseEntryType(r.ODataType),
        TemplateName = r.TemplateName,
        TemplateId = r.TemplateId,
        FileSizeBytes = r.FileSizeBytes ?? r.ElectronicDocumentSize ?? r.ElecDocumentSize,
        PageCount = r.PageCount,
        RowNumber = r.RowNumber
    };

    private static LFEntryType ParseEntryType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return LFEntryType.Unknown;

        var token = raw.Trim().TrimStart('#');
        var dot = token.LastIndexOf('.');
        if (dot >= 0)
            token = token[(dot + 1)..];

        return token.ToLowerInvariant() switch
        {
            "document" => LFEntryType.Document,
            "folder" => LFEntryType.Folder,
            "shortcut" => LFEntryType.Shortcut,
            "recordseries" => LFEntryType.RecordSeries,
            _ => LFEntryType.Unknown
        };
    }

    private sealed record EntryPage(List<LFEntry> Entries, string? NextLink);

    private sealed record ODataPagedList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }

        [JsonPropertyName("nextLink")]
        public string? PlainNextLink { get; init; }
    }

    private sealed record EntryResource
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("parentId")]
        public int ParentId { get; init; }

        [JsonPropertyName("fullPath")]
        public string FullPath { get; init; } = string.Empty;

        [JsonPropertyName("folderPath")]
        public string FolderPath { get; init; } = string.Empty;

        [JsonPropertyName("creator")]
        public string? Creator { get; init; }

        [JsonPropertyName("creationTime")]
        public DateTimeOffset? CreationTime { get; init; }

        [JsonPropertyName("createdTime")]
        public DateTimeOffset? CreatedTime { get; init; }

        [JsonPropertyName("lastModifiedTime")]
        public DateTimeOffset? LastModifiedTime { get; init; }

        [JsonPropertyName("modifiedTime")]
        public DateTimeOffset? ModifiedTime { get; init; }

        [JsonPropertyName("entryType")]
        public string? EntryType { get; init; }

        [JsonPropertyName("@odata.type")]
        public string? ODataType { get; init; }

        [JsonPropertyName("templateName")]
        public string? TemplateName { get; init; }

        [JsonPropertyName("templateId")]
        public int? TemplateId { get; init; }

        [JsonPropertyName("fileSizeBytes")]
        public long? FileSizeBytes { get; init; }

        [JsonPropertyName("electronicDocumentSize")]
        public long? ElectronicDocumentSize { get; init; }

        [JsonPropertyName("elecDocumentSize")]
        public long? ElecDocumentSize { get; init; }

        [JsonPropertyName("pageCount")]
        public int? PageCount { get; init; }

        [JsonPropertyName("rowNumber")]
        public int? RowNumber { get; init; }
    }

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
