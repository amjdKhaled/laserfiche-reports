using System.Text.Json;
using System.Text.Json.Serialization;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Retrieves every template definition from the active Laserfiche repository.
/// Both bare-array and OData paged responses are supported so TotalTemplates is
/// never silently limited to the first server page.
/// </summary>
internal sealed class LaserficheTemplateService : ILaserficheTemplateService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILaserficheApiAdapter _adapter;
    private readonly ILogger<LaserficheTemplateService> _logger;

    public LaserficheTemplateService(
        IHttpClientFactory httpClientFactory,
        IRepositoryContext repositoryContext,
        ILaserficheApiAdapter adapter,
        ILogger<LaserficheTemplateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repositoryContext = repositoryContext;
        _adapter = adapter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LFTemplateDefinition>> GetTemplateDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var firstUrl = _adapter.BuildTemplateDefinitionsUrl(repo.RepositoryId);
        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        var resources = new List<TemplateDefinitionResource>();
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextUrl = firstUrl;
        var page = 0;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!visitedUrls.Add(nextUrl))
            {
                throw new LaserficheException(
                    $"TemplateDefinitions pagination returned a repeated nextLink: {nextUrl}",
                    500);
            }

            page++;
            _logger.LogDebug("Fetching template definitions page {Page}: {Url}", page, nextUrl);

            using var response = await client.GetAsync(nextUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LaserficheException(
                    $"Laserfiche API returned HTTP {(int)response.StatusCode} while reading " +
                    $"TemplateDefinitions page {page} at {nextUrl}. Body: {body}",
                    (int)response.StatusCode);
            }

            var parsed = ParsePage(body);
            resources.AddRange(parsed.Items);
            nextUrl = ResolveNextLink(nextUrl, parsed.NextLink);

            _logger.LogInformation(
                "TemplateDefinitions page {Page}: {PageCount} item(s), running total={Total}, nextLink={HasNext}.",
                page, parsed.Items.Count, resources.Count, nextUrl is null ? "no" : "yes");
        }

        var templates = resources
            .Where(t => t.Id > 0 && !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Id)
            .Select(g => g.Last())
            .Select(t => new LFTemplateDefinition
            {
                Id          = t.Id,
                Name        = t.Name.Trim(),
                Description = t.Description
            })
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        _logger.LogInformation(
            "Loaded {Count} unique template definitions from repository {RepositoryId} across {Pages} page(s).",
            templates.Count, repo.RepositoryId, page);

        return templates;
    }

    private static TemplatePage ParsePage(string body)
    {
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new JsonException("TemplateDefinitions response body was empty.");

        if (body.StartsWith('['))
        {
            var items = JsonSerializer.Deserialize<List<TemplateDefinitionResource>>(
                body, JsonOptions.Default) ?? [];
            return new TemplatePage(items, null);
        }

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.EnumerateObject().Any(p =>
                string.Equals(p.Name, "value", StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind == JsonValueKind.Array))
            throw new JsonException("TemplateDefinitions response did not contain a value array.");

        var result = JsonSerializer.Deserialize<ODataList<TemplateDefinitionResource>>(
            body, JsonOptions.Default)
            ?? throw new JsonException("TemplateDefinitions response could not be deserialized.");

        return new TemplatePage(result.Value, result.NextLink ?? result.PlainNextLink);
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
                $"TemplateDefinitions nextLink points outside the active Laserfiche API host: {nextLink}");
        }

        return resolved.AbsoluteUri;
    }

    private sealed record TemplatePage(
        List<TemplateDefinitionResource> Items,
        string? NextLink);

    private sealed record ODataList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }

        [JsonPropertyName("nextLink")]
        public string? PlainNextLink { get; init; }
    }

    private sealed record TemplateDefinitionResource
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
