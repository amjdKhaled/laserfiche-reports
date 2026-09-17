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
/// Provides entry-level operations by calling the Laserfiche Repository API v1
/// <c>/Entries</c> endpoints. All data is sourced directly from the live API.
/// </summary>
internal sealed class LaserficheEntryService : ILaserficheEntryService
{
    // Process-lifetime cache: repositoryId → root entry ID.
    // The root never changes while the server is running, so a static cache is safe.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int>
        s_rootIdCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILaserficheApiAdapter _adapter;
    private readonly ILogger<LaserficheEntryService> _logger;

    /// <summary>Initialises the service with all required dependencies.</summary>
    public LaserficheEntryService(
        IHttpClientFactory httpClientFactory,
        IRepositoryContext repositoryContext,
        ILaserficheApiAdapter adapter,
        ILogger<LaserficheEntryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repositoryContext = repositoryContext;
        _adapter = adapter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LFEntry> GetEntryAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext.GetActiveRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var url = _adapter.BuildEntryUrl(repo.RepositoryId, entryId, Adapters.EntryResource.Details);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, url, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var resource = JsonSerializer.Deserialize<EntryApiResource>(body, JsonOptions.Default)
            ?? throw new LaserficheException("Entry response was empty.", (int)response.StatusCode);

        return MapEntry(resource);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LFFieldValue>> GetEntryFieldsAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext.GetActiveRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var url = _adapter.BuildEntryUrl(repo.RepositoryId, entryId, Adapters.EntryResource.Fields);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "(missing)";

        // Keep this complete diagnostic immediately after the HTTP response and
        // before any deserialization. The fields endpoint has multiple response
        // shapes across Repository API v1 builds, so a DTO parse must never be
        // allowed to hide a successful response that contains field data.
        _logger.LogInformation(
            "===== ENTRY FIELDS RAW RESPONSE =====\n" +
            "Repository: {Repository}\n" +
            "EntryId: {EntryId}\n" +
            "URL: {Url}\n" +
            "HTTP Status: {Status}\n" +
            "Content-Type: {ContentType}\n" +
            "Raw Body:\n{Body}\n" +
            "=======================================",
            repo.RepositoryId,
            entryId,
            url,
            (int)response.StatusCode,
            contentType,
            body);

        if (!response.IsSuccessStatusCode)
        {
            throw new LaserficheException(
                $"Laserfiche API returned HTTP {(int)response.StatusCode} for {url}. Body: {body}",
                (int)response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogInformation(
                "ARCHIVE METADATA DIAGNOSTIC: EntryId={EntryId} FieldsApiStatus=HTTP {Status} " +
                "RawFields=0 ParsedFields=0 FieldDefinitions=resolved later",
                entryId, (int)response.StatusCode);
            return [];
        }

        var resources = ParseFieldResources(body);
        var parsed = resources.Select(MapFieldValue).ToList().AsReadOnly();

        _logger.LogInformation(
            "ARCHIVE METADATA DIAGNOSTIC: EntryId={EntryId} FieldsApiStatus=HTTP {Status} " +
            "RawFields={RawCount} ParsedFields={ParsedCount} fieldDefinitionIds=[{Ids}]",
            entryId,
            (int)response.StatusCode,
            resources.Count,
            parsed.Count,
            string.Join(", ", parsed.Select(f => f.FieldDefinitionId)));

        return parsed;
    }

    /// <inheritdoc />
    public async Task<LFTemplate?> GetEntryTemplateAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false);

        if (entry.TemplateId is null || string.IsNullOrWhiteSpace(entry.TemplateName))
        {
            return null;
        }

        var fields = await GetEntryFieldsAsync(entryId, cancellationToken).ConfigureAwait(false);

        return new LFTemplate
        {
            Id   = entry.TemplateId.Value,
            Name = entry.TemplateName,
            Fields = fields.Select(f => new LFFieldDefinition
            {
                Name        = f.FieldName,
                FieldType   = f.FieldType ?? "String",
                IsRequired  = f.IsRequired,
                IsMultiValue = f.IsMultiValue
            }).ToList().AsReadOnly()
        };
    }

    /// <inheritdoc />
    public async Task<string> GetEntryPathAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(entryId, cancellationToken).ConfigureAwait(false);
        return entry.FullPath;
    }

    /// <inheritdoc />
    public async Task<PagedResult<LFEntry>> GetEntryChildrenAsync(
        int entryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext.GetActiveRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var skip = (page - 1) * pageSize;
        // Use the Swagger-documented OData-typed folder-children path.
        // No OData query parameters — this server returns HTTP 400 for $top/$skip/$count/$select
        // on the folder-children endpoint. Fetch all children and slice in memory.
        var url = _adapter.BuildFolderChildrenUrl(repo.RepositoryId, entryId);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, url, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var allEntries = ParseEntryList(body);
        var totalCount = allEntries.Count;

        // Server does not support $top/$skip — slice in memory.
        var page_items = allEntries.Skip(skip).Take(pageSize).ToList();

        return new PagedResult<LFEntry>
        {
            Items      = page_items.AsReadOnly(),
            TotalCount = totalCount,
            PageNumber = page,
            PageSize   = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<int> GetRootEntryIdAsync(CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        if (s_rootIdCache.TryGetValue(repo.RepositoryId, out int cached))
        {
            _logger.LogDebug("Root entry ID for {RepoId} served from cache: {Id}.", repo.RepositoryId, cached);
            return cached;
        }

        // ALWAYS discover the authoritative root via ByPath("\\").
        //
        // DO NOT short-circuit on configuredRootId=1. The default value of 1 is not
        // guaranteed to be the repository root on every Laserfiche installation.
        // ByPath always gives the correct root ID.
        //
        // NOTE: even if the root ID really is 1, we still call ByPath so that the
        // correct version-specific children URL (V2: Folder/Children, V1:
        // Laserfiche.Repository.Folder/children) is used downstream.
        //
        // The configured value becomes a fallback ONLY when ByPath fails.
        var configuredRootId = _adapter.GetConfiguredRootEntryId();

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        var byPathUrl = _adapter.BuildEntryByPathUrl(repo.RepositoryId, @"\");
        _logger.LogInformation(
            "SCAN — Discovering repository root via ByPath: {Url} (configuredRootId={ConfigId})",
            byPathUrl, configuredRootId);

        try
        {
            using var response = await client.GetAsync(byPathUrl, cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "===== RAW BYPATH RESPONSE (HTTP {Status}) =====\n{Body}\n===============================================",
                (int)response.StatusCode, body);

            if (response.IsSuccessStatusCode)
            {
                var rootId = TryParseByPathId(body, byPathUrl);
                if (rootId > 0)
                {
                    s_rootIdCache[repo.RepositoryId] = rootId;
                    return rootId;
                }
            }
            else
            {
                _logger.LogWarning(
                    "SCAN — ByPath root discovery failed: HTTP {Status} from {Url}. Body: {Body}",
                    (int)response.StatusCode, byPathUrl,
                    body.Length > 400 ? body[..400] + "…" : body);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SCAN — ByPath root discovery threw. Will fall back to configuredRootId={Id}.",
                configuredRootId);
        }

        // ── Fallback: administrator-configured root entry ID ──────────────────
        if (configuredRootId > 0)
        {
            _logger.LogWarning(
                "SCAN — Using configured root entry ID={Id} for repository '{RepoId}' " +
                "as fallback (ByPath discovery did not return a usable ID). " +
                "Set RootEntryId=0 in Settings to force ByPath discovery on every load.",
                configuredRootId, repo.RepositoryId);
            s_rootIdCache[repo.RepositoryId] = configuredRootId;
            return configuredRootId;
        }

        throw new LaserficheException(
            $"Root entry discovery failed for repository '{repo.RepositoryId}'. " +
            $"ByPath returned an unexpected response and no valid RootEntryId is configured in Settings.",
            500);
    }

    /// <summary>
    /// Parses the root entry ID from a ByPath response body.
    /// Handles two response shapes:
    /// <list type="bullet">
    ///   <item>Wrapped:  <c>{{ "entry": {{ "id": N, ... }} }}</c>  — used by some v1/v2 builds.</item>
    ///   <item>Direct:   <c>{{ "id": N, "entryType": "Folder", ... }}</c>  — used by some v2 builds.</item>
    /// </list>
    /// Returns 0 when neither shape can be parsed.
    /// </summary>
    private int TryParseByPathId(string body, string url)
    {
        // ── Try wrapped shape: {"entry": {"id": N, ...}} ─────────────────────
        try
        {
            var wrapped = JsonSerializer.Deserialize<ByPathApiResponse>(body, JsonOptions.Default);
            if (wrapped?.Entry is { Id: > 0 } wrappedEntry)
            {
                _logger.LogInformation(
                    "SCAN — ByPath (wrapped shape): root id={Id}, name='{Name}', path='{Path}'.",
                    wrappedEntry.Id, wrappedEntry.Name, wrappedEntry.FullPath);
                return wrappedEntry.Id;
            }
        }
        catch (JsonException) { /* fall through to direct shape */ }

        // ── Try direct shape: {"id": N, "entryType": "Folder", ...} ──────────
        try
        {
            var direct = JsonSerializer.Deserialize<EntryApiResource>(body, JsonOptions.Default);
            if (direct is { Id: > 0 })
            {
                _logger.LogInformation(
                    "SCAN — ByPath (direct shape): root id={Id}, name='{Name}', path='{Path}'.",
                    direct.Id, direct.Name, direct.FullPath);
                return direct.Id;
            }
        }
        catch (JsonException) { /* fall through */ }

        _logger.LogWarning(
            "SCAN — ByPath response from {Url} could not be parsed for a root entry ID. " +
            "Body: {Body}",
            url, body.Length > 400 ? body[..400] + "…" : body);
        return 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LFEntry>> GetAllFolderChildrenAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        // Version-aware endpoint:
        //   V2: GET /Repositories/{repoId}/Entries/{id}/Folder/Children
        //   V1: GET /Repositories/{repoId}/Entries/{id}/Laserfiche.Repository.Folder/children
        var firstUrl = _adapter.BuildFolderChildrenUrl(repo.RepositoryId, entryId);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        var allEntries = new List<LFEntry>();
        string? nextUrl = firstUrl;
        int pageNumber  = 0;
        const int PageCap = 50;   // safety cap — prevents infinite nextLink loops

        while (nextUrl is not null && pageNumber < PageCap)
        {
            pageNumber++;
            string body;
            try
            {
                using var response = await client.GetAsync(nextUrl, cancellationToken).ConfigureAwait(false);

                body = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}, page={Page}): " +
                        "GET {Url} → HTTP {Status}. Body: {Body}",
                        entryId, pageNumber, nextUrl, (int)response.StatusCode,
                        body.Length > 400 ? body[..400] + "…" : body);
                    break;  // stop pagination on error
                }

                _logger.LogInformation(
                    "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}, page={Page}): GET {Url} → HTTP {Status}.",
                    entryId, pageNumber, nextUrl, (int)response.StatusCode);

                // Log first page raw body for schema inspection
                if (pageNumber == 1)
                {
                    _logger.LogInformation(
                        "===== RAW FOLDER-CHILDREN RESPONSE (entryId={EntryId}, page=1) =====\n{Body}\n==========================================================",
                        entryId, body.Length > 4000 ? body[..4000] + "\n…[truncated]" : body);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}, page={Page}): GET {Url} threw.",
                    entryId, pageNumber, nextUrl);
                break;
            }

            try
            {
                var (pageEntries, next) = ParseEntryListWithNextLink(body);

                if (pageEntries.Count == 0 && pageNumber == 1)
                {
                    _logger.LogWarning(
                        "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}): HTTP 200 but page 1 produced 0 entries. " +
                        "The response schema may not match the expected OData envelope {{\"value\":[...]}}. " +
                        "Raw body: {Body}",
                        entryId, body.Length > 4000 ? body[..4000] + "…" : body);
                }

                allEntries.AddRange(pageEntries);
                _logger.LogInformation(
                    "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}, page={Page}): " +
                    "+{PageCount} entries (running total={Total}), nextLink={HasNext}.",
                    entryId, pageNumber, pageEntries.Count, allEntries.Count, next is not null ? "yes" : "no");

                // Sanitise nextLink — must be an absolute URL that we understand.
                nextUrl = ResolveNextLink(nextUrl, next);
                if (nextUrl is not null)
                {
                    _logger.LogInformation(
                        "SCAN — Pagination found for folder {EntryId}; following page {NextPage}.",
                        entryId, pageNumber + 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}, page={Page}): " +
                    "JSON parse failed. Raw body: {Body}",
                    entryId, pageNumber, body.Length > 4000 ? body[..4000] + "…" : body);
                break;
            }
        }

        if (pageNumber >= PageCap)
        {
            _logger.LogWarning(
                "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}): pagination safety cap ({Cap} pages) reached.",
                entryId, PageCap);
        }

        _logger.LogInformation(
            "SCAN — GetAllFolderChildrenAsync(entryId={EntryId}): " +
            "total {Count} entries across {Pages} page(s). Types: {Types}",
            entryId, allEntries.Count, pageNumber,
            string.Join(", ", allEntries.Take(5).Select(e => e.EntryType.ToString())));

        return allEntries.AsReadOnly();
    }

    /// <summary>
    /// Returns true when <paramref name="nextLink"/> is a non-empty absolute URL
    /// that can safely be used as the next-page request.
    /// Rejects relative URLs, fragment-only strings, and obvious duplicates.
    /// </summary>
    private static string? ResolveNextLink(string currentUrl, string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink) ||
            !Uri.TryCreate(currentUrl, UriKind.Absolute, out var current))
            return null;

        if (!Uri.TryCreate(current, nextLink, out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(resolved.Scheme, current.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Authority, current.Authority, StringComparison.OrdinalIgnoreCase))
            return null;

        return resolved.AbsoluteUri;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LFEntry>> GetFolderTreeAsync(
        int rootEntryId,
        int depth,
        CancellationToken cancellationToken = default)
    {
        if (depth is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "Folder tree depth must be between 1 and 5.");
        }

        var result = new List<LFEntry>();
        await TraverseFolderAsync(rootEntryId, depth, 0, result, cancellationToken).ConfigureAwait(false);
        return result.AsReadOnly();
    }

    /// <summary>
    /// Recursively traverses folders up to <paramref name="maxDepth"/> levels deep,
    /// collecting folder entries into <paramref name="accumulator"/>.
    /// </summary>
    private async Task TraverseFolderAsync(
        int folderId,
        int maxDepth,
        int currentDepth,
        List<LFEntry> accumulator,
        CancellationToken cancellationToken)
    {
        if (currentDepth >= maxDepth) return;

        var page = await GetEntryChildrenAsync(folderId, 1, 200, cancellationToken)
            .ConfigureAwait(false);

        foreach (var child in page.Items)
        {
            if (child.EntryType == LFEntryType.Folder)
            {
                accumulator.Add(child);
                await TraverseFolderAsync(
                    child.Id, maxDepth, currentDepth + 1, accumulator, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    // ──────────────────────────── Mapping ─────────────────────────────────

    private static LFEntry MapEntry(EntryApiResource r) => new()
    {
        Id                 = r.Id,
        Name               = r.Name,
        ParentId           = r.ParentId,
        FullPath           = r.FullPath,
        FolderPath         = r.FolderPath,
        Creator            = r.Creator,
        CreationTime       = r.CreationTime ?? r.CreatedTime,
        LastModifiedTime   = r.LastModifiedTime ?? r.ModifiedTime,
        // Prefer the explicit "entryType" field; fall back to the OData "@odata.type"
        // discriminator (e.g. "#Laserfiche.Repository.Folder") when entryType is absent.
        EntryType          = ParseEntryType(r.EntryType ?? r.ODataType),
        TemplateName       = r.TemplateName,
        TemplateId         = r.TemplateId,
        FileSizeBytes      = r.FileSizeBytes,
        PageCount          = r.PageCount,
        RowNumber          = r.RowNumber
    };

    /// <summary>
    /// Parses a Laserfiche API response into a list of <see cref="LFEntry"/> items and
    /// an optional <c>@odata.nextLink</c> URL for V2 paginated responses.
    /// Handles both formats:
    ///   • OData envelope: <c>{"value":[...], "@odata.nextLink":"..."}</c>  — /children, /Searches, etc.
    ///   • Bare array:     <c>[...]</c>                                     — legacy v1 builds.
    /// </summary>
    private (List<LFEntry> Entries, string? NextLink) ParseEntryListWithNextLink(string body)
    {
        body = body.Trim();

        if (body.StartsWith('['))
        {
            // Bare JSON array — no pagination
            var resources = JsonSerializer.Deserialize<List<EntryApiResource>>(body, JsonOptions.Default) ?? [];
            return (resources.Select(MapEntry).ToList(), null);
        }

        // OData envelope {"value":[...], "@odata.nextLink":"..."}
        var odata = JsonSerializer.Deserialize<ODataPagedList<EntryApiResource>>(body, JsonOptions.Default);
        var entries = (odata?.Value ?? []).Select(MapEntry).ToList();
        return (entries, odata?.NextLink ?? odata?.PlainNextLink);
    }

    /// <summary>
    /// Parses a Laserfiche API response into a flat entry list (no pagination link returned).
    /// Used by callers that read all-at-once (e.g. <see cref="GetEntryChildrenAsync"/>).
    /// </summary>
    private List<LFEntry> ParseEntryList(string body)
    {
        var (entries, _) = ParseEntryListWithNextLink(body);
        return entries;
    }

    /// <summary>
    /// Maps the raw API string to <see cref="LFEntryType"/>.
    /// Handles the two forms returned by different Laserfiche API builds:
    ///   • Simple names: "Document", "Folder", "Shortcut", "RecordSeries"
    ///   • OData qualified names: "#Laserfiche.Repository.Document", etc.
    /// </summary>
    private static LFEntryType ParseEntryType(string? raw)
    {
        if (raw is null) return LFEntryType.Unknown;

        // Strip leading '#' and extract the last segment after '.'
        // e.g. "#Laserfiche.Repository.Document" → "document"
        var token = raw.TrimStart('#');
        var dot   = token.LastIndexOf('.');
        if (dot >= 0) token = token[(dot + 1)..];

        return token.ToLowerInvariant() switch
        {
            "document"     => LFEntryType.Document,
            "folder"       => LFEntryType.Folder,
            "shortcut"     => LFEntryType.Shortcut,
            "recordseries" => LFEntryType.RecordSeries,
            _              => LFEntryType.Unknown
        };
    }

    /// <summary>
    /// Parses the Entry fields response without assuming one particular v1 envelope.
    /// Confirmed v1 installations may return a bare array, an OData value envelope,
    /// or a named fields/fieldValues collection. Each object is retained even when
    /// an optional property is absent; name resolution happens later via
    /// FieldDefinitions.
    /// </summary>
    private static List<FieldResource> ParseFieldResources(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 TryGetArray(root, "value", out var valueArray))
        {
            array = valueArray;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 TryGetArray(root, "fields", out var fieldsArray))
        {
            array = fieldsArray;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 TryGetArray(root, "fieldValues", out var fieldValuesArray))
        {
            array = fieldValuesArray;
        }
        else
        {
            throw new JsonException(
                "Entry fields response did not contain a JSON array, value array, " +
                "fields array, or fieldValues array.");
        }

        var resources = new List<FieldResource>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                resources.Add(ParseFieldResource(item));
            }
        }

        return resources;
    }

    private static bool TryGetArray(
        JsonElement objectElement,
        string propertyName,
        out JsonElement array)
    {
        if (objectElement.TryGetProperty(propertyName, out array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }

    private static FieldResource ParseFieldResource(JsonElement item)
    {
        // Confirmed Laserfiche v1 EntryFieldValue shape:
        // fieldId, fieldName, fieldType, and values[].value.
        // Keep the older property names as compatibility fallbacks for other
        // Repository API v1 builds.
        var definitionId = ReadInt(item, "fieldId")
                           ?? ReadInt(item, "fieldDefinitionId");
        var inlineName   = ReadString(item, "fieldName")
                           ?? ReadString(item, "name");
        var fieldType    = ReadString(item, "fieldType");

        // Some API responses nest definition metadata:
        // { "fieldDefinition": { "id": 17, "name": "First Name" }, ... }.
        if (TryGetPropertyIgnoreCase(item, "fieldDefinition", out var definition) &&
            definition.ValueKind == JsonValueKind.Object)
        {
            definitionId ??= ReadInt(definition, "id");
            inlineName ??= ReadString(definition, "name");
            fieldType ??= ReadString(definition, "fieldType");
        }

        var value = ReadValue(item);

        return new FieldResource
        {
            FieldDefinitionId = definitionId ?? 0,
            Name              = inlineName ?? string.Empty,
            Value             = value,
            FieldType         = fieldType,
            IsRequired         = ReadBool(item, "isRequired"),
            IsMultiValue       = ReadBool(item, "isMultiValue")
        };
    }

    private static LFFieldValue MapFieldValue(FieldResource resource) => new()
    {
        FieldDefinitionId = resource.FieldDefinitionId,
        // May be empty; ArchiveController resolves the authoritative name
        // from the repository-wide FieldDefinitions response.
        FieldName         = resource.Name,
        Value             = resource.Value,
        FieldType         = resource.FieldType,
        IsRequired        = resource.IsRequired,
        IsMultiValue      = resource.IsMultiValue
    };

    private static string? ReadValue(JsonElement item)
    {
        // Confirmed response shape:
        // "values": [{ "value": "..." , "position": 0 }]
        // A document field can have multiple values, so preserve all of them
        // in display order instead of reading only the first one.
        if (TryGetPropertyIgnoreCase(item, "values", out var values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            var valuesList = values.EnumerateArray()
                .Select(valueItem =>
                {
                    if (valueItem.ValueKind == JsonValueKind.Object &&
                        TryGetPropertyIgnoreCase(valueItem, "value", out var nestedValue))
                    {
                        return JsonElementToString(nestedValue);
                    }

                    return JsonElementToString(valueItem);
                })
                .ToList();

            return valuesList.Count == 0
                ? null
                : string.Join(", ", valuesList);
        }

        if (!TryGetPropertyIgnoreCase(item, "value", out var value))
        {
            if (TryGetPropertyIgnoreCase(item, "valueText", out var valueText))
            {
                return valueText.ValueKind == JsonValueKind.Null
                    ? null
                    : valueText.ToString();
            }

            return null;
        }

        return JsonElementToString(value);
    }

    private static string? JsonElementToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null      => null,
            JsonValueKind.Array     => string.Join(", ", value.EnumerateArray().Select(v => v.ToString())),
            JsonValueKind.Object    => value.GetRawText(),
            _                       => value.ToString()
        };
    }

    private static int? ReadInt(JsonElement item, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(item, propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), out number) ? number : null;
    }

    private static string? ReadString(JsonElement item, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(item, propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ToString();
    }

    private static bool ReadBool(JsonElement item, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(item, propertyName, out var value)) return false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return bool.TryParse(value.ToString(), out var result) && result;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement objectElement,
        string propertyName,
        out JsonElement value)
    {
        if (objectElement.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in objectElement.EnumerateObject())
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

    // ──────────────────────────── Helpers ─────────────────────────────────

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string url,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new LaserficheException(
            $"Laserfiche API returned HTTP {(int)response.StatusCode} for {url}. Body: {body}",
            (int)response.StatusCode);
    }

    // ──────────────────────────── Response models ──────────────────────────

    /// <summary>
    /// Wrapper returned by <c>GET /Repositories/{repo}/Entries/ByPath</c>.
    /// Schema: <c>{ "entry": { "id": N, "entryType": "...", ... } }</c>
    /// </summary>
    private sealed record ByPathApiResponse
    {
        [JsonPropertyName("entry")]
        public EntryApiResource? Entry { get; init; }
    }

    private sealed record ODataList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];
    }

    /// <summary>
    /// OData response envelope that captures the <c>@odata.nextLink</c> continuation URL
    /// returned by V2 paginated endpoints (folder children, search results, etc.).
    /// </summary>
    private sealed record ODataPagedList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        /// <summary>
        /// Absolute URL for the next page of results, or <c>null</c> when this is the
        /// last page.  Only present when the server paginates the response.
        /// </summary>
        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }

        [JsonPropertyName("nextLink")]
        public string? PlainNextLink { get; init; }
    }

    private sealed record ODataCountList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.count")]
        public int Count { get; init; }
    }

    private sealed record EntryApiResource
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

        /// <summary>OData type discriminator, e.g. "#Laserfiche.Repository.Folder".</summary>
        [JsonPropertyName("@odata.type")]
        public string? ODataType { get; init; }

        [JsonPropertyName("templateName")]
        public string? TemplateName { get; init; }

        [JsonPropertyName("templateId")]
        public int? TemplateId { get; init; }

        [JsonPropertyName("fileSizeBytes")]
        public long? FileSizeBytes { get; init; }

        [JsonPropertyName("pageCount")]
        public int? PageCount { get; init; }

        [JsonPropertyName("rowNumber")]
        public int? RowNumber { get; init; }
    }

    private sealed record FieldResource
    {
        /// <summary>
        /// Numeric field definition ID. Matches <c>id</c> in the repository-wide
        /// FieldDefinitions list. Used as the join key when resolving human-readable names.
        /// </summary>
        [JsonPropertyName("fieldDefinitionId")]
        public int FieldDefinitionId { get; init; }

        /// <summary>
        /// Human-readable field name. May be populated by some server builds directly
        /// in the entry fields response; used as a fallback if a FieldDefinitions lookup
        /// is unavailable.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public string? Value { get; init; }

        [JsonPropertyName("fieldType")]
        public string? FieldType { get; init; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; init; }

        [JsonPropertyName("isMultiValue")]
        public bool IsMultiValue { get; init; }
    }
}
