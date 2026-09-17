using System.Text.Json;
using System.Text.Json.Serialization;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Retrieves every repository-wide field definition from the active Laserfiche Repository API,
/// following all server-provided continuation links.
/// </summary>
internal sealed class LaserficheFieldDefinitionService : ILaserficheFieldDefinitionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILaserficheApiAdapter _adapter;
    private readonly ILogger<LaserficheFieldDefinitionService> _logger;

    public LaserficheFieldDefinitionService(
        IHttpClientFactory httpClientFactory,
        IRepositoryContext repositoryContext,
        ILaserficheApiAdapter adapter,
        ILogger<LaserficheFieldDefinitionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repositoryContext = repositoryContext;
        _adapter = adapter;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<int, LFFieldDefinition>> GetFieldDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var firstUrl = _adapter.BuildFieldDefinitionsUrl(repo.RepositoryId);
        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        var resources = new List<FieldDefinitionResource>();
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextUrl = firstUrl;
        var page = 0;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!visitedUrls.Add(nextUrl))
            {
                throw new LaserficheException(
                    $"FieldDefinitions pagination returned a repeated nextLink: {nextUrl}",
                    500);
            }

            page++;
            using var response = await client.GetAsync(nextUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LaserficheException(
                    $"Laserfiche API returned HTTP {(int)response.StatusCode} while reading " +
                    $"FieldDefinitions page {page} at {nextUrl}. Body: {body}",
                    (int)response.StatusCode);
            }

            var parsed = ParsePage(body);
            resources.AddRange(parsed.Items);
            nextUrl = ResolveNextLink(nextUrl, parsed.NextLink);

            _logger.LogInformation(
                "FieldDefinitions page {Page}: {PageCount} item(s), running total={Total}, nextLink={HasNext}.",
                page, parsed.Items.Count, resources.Count, nextUrl is null ? "no" : "yes");
        }

        // Definition ID is the repository's authoritative join key. If a page boundary
        // repeats a definition, retain the latest representation rather than throwing.
        var definitions = resources
            .Where(r => r.Id > 0)
            .GroupBy(r => r.Id)
            .Select(g => g.Last())
            .ToDictionary(r => r.Id, r => new LFFieldDefinition
            {
                Id = r.Id,
                Name = r.Name,
                FieldType = r.FieldType ?? string.Empty,
                IsRequired = r.IsRequired,
                IsMultiValue = r.IsMultiValue,
                Description = r.Description,
                MaxLength = r.MaxLength
            })
            .AsReadOnly();

        _logger.LogInformation(
            "Loaded {Count} unique field definitions from repository {RepositoryId} across {Pages} page(s).",
            definitions.Count, repo.RepositoryId, page);

        return definitions;
    }

    private static FieldPage ParsePage(string body)
    {
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new JsonException("FieldDefinitions response body was empty.");

        if (body.StartsWith('['))
        {
            var items = JsonSerializer.Deserialize<List<FieldDefinitionResource>>(
                body, JsonOptions.Default) ?? [];
            return new FieldPage(items, null);
        }

        var odata = JsonSerializer.Deserialize<ODataList<FieldDefinitionResource>>(
            body, JsonOptions.Default)
            ?? throw new JsonException("FieldDefinitions response could not be deserialized.");

        return new FieldPage(odata.Value, odata.NextLink ?? odata.PlainNextLink);
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
                $"FieldDefinitions nextLink points outside the active Laserfiche API host: {nextLink}");
        }

        return resolved.AbsoluteUri;
    }

    private sealed record FieldPage(List<FieldDefinitionResource> Items, string? NextLink);

    private sealed record ODataList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }

        [JsonPropertyName("nextLink")]
        public string? PlainNextLink { get; init; }
    }

    private sealed record FieldDefinitionResource
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("fieldType")]
        public string? FieldType { get; init; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; init; }

        [JsonPropertyName("isMultiValue")]
        public bool IsMultiValue { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; init; }
    }

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
