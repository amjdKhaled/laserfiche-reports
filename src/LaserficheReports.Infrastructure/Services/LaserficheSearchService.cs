using System.Net.Http.Json;
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
/// Implements Laserfiche search operations using Repository API server-side paging.
/// </summary>
internal sealed class LaserficheSearchService : ILaserficheSearchService
{
    private const int MaxPollDurationSeconds = 30;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILaserficheApiAdapter _adapter;
    private readonly ISearchAuditLog _auditLog;
    private readonly ILogger<LaserficheSearchService> _logger;

    public LaserficheSearchService(
        IHttpClientFactory httpClientFactory,
        IRepositoryContext repositoryContext,
        ILaserficheApiAdapter adapter,
        ISearchAuditLog auditLog,
        ILogger<LaserficheSearchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repositoryContext = repositoryContext;
        _adapter = adapter;
        _auditLog = auditLog;
        _logger = logger;
    }

    public Task<PagedResult<LFSearchResult>> SimpleSearchAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ExecuteSearchAsync(
            query,
            SearchType.Simple,
            $"{{LF:Basic}}=\"{EscapeSearchTerm(query)}\"",
            page,
            pageSize,
            cancellationToken);

    public Task<PagedResult<LFSearchResult>> AdvancedSearchAsync(
        string searchExpression,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ExecuteSearchAsync(
            searchExpression,
            SearchType.Advanced,
            searchExpression,
            page,
            pageSize,
            cancellationToken);

    public Task<PagedResult<LFSearchResult>> SearchByTemplateAsync(
        string templateName,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ExecuteSearchAsync(
            templateName,
            SearchType.Advanced,
            $"{{LF:Template}}=\"{EscapeSearchTerm(templateName)}\"",
            page,
            pageSize,
            cancellationToken);

    public Task<PagedResult<LFSearchResult>> SearchByFieldAsync(
        string fieldName,
        string fieldValue,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ExecuteSearchAsync(
            $"{fieldName}={fieldValue}",
            SearchType.Advanced,
            $"{{{EscapeSearchTerm(fieldName)}}}=\"{EscapeSearchTerm(fieldValue)}\"",
            page,
            pageSize,
            cancellationToken);

    private async Task<PagedResult<LFSearchResult>> ExecuteSearchAsync(
        string displayQuery,
        SearchType searchType,
        string expression,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidatePaging(page, pageSize);

        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        await _auditLog
            .RecordSearchAsync(repo.RepositoryId, displayQuery, cancellationToken)
            .ConfigureAwait(false);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");
        var searchUrl = _adapter.BuildSearchUrl(repo.RepositoryId, searchType);
        var requestBody = new { searchCommand = expression };

        _logger.LogInformation(
            "Search submit → POST {Url} | query: {Query}",
            searchUrl,
            displayQuery);

        using var submitResponse = await client
            .PostAsJsonAsync(searchUrl, requestBody, JsonOptions.Default, cancellationToken)
            .ConfigureAwait(false);

        var submitBody = await submitResponse.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!submitResponse.IsSuccessStatusCode)
        {
            throw new LaserficheException(
                $"Search failed with HTTP {(int)submitResponse.StatusCode}. " +
                $"Query: {displayQuery}. Body: {submitBody}",
                (int)submitResponse.StatusCode);
        }

        using var submitDoc = JsonDocument.Parse(submitBody);
        if (submitDoc.RootElement.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(submitDoc.RootElement, "value", out var value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return await ReadRequestedPageAsync(client, submitBody, searchUrl, page, pageSize, cancellationToken)
                .ConfigureAwait(false);
        }

        var taskResult = JsonSerializer.Deserialize<LongOperationResponse>(submitBody, JsonOptions.Default)
            ?? throw new JsonException("Search submit response could not be deserialized.");

        if (string.IsNullOrWhiteSpace(taskResult.OperationToken) &&
            string.IsNullOrWhiteSpace(taskResult.TaskId) &&
            string.IsNullOrWhiteSpace(taskResult.Token))
        {
            throw new LaserficheException(
                $"Search for '{displayQuery}' returned neither an inline result collection nor an operation token. " +
                $"Body: {submitBody}",
                200);
        }

        if (taskResult.Status?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new LaserficheException(
                $"Laserfiche search operation failed for query: {displayQuery}. " +
                $"Errors: {string.Join("; ", taskResult.Errors)}",
                500);
        }

        var token = taskResult.OperationToken ?? taskResult.TaskId ?? taskResult.Token!;
        if (taskResult.Status?.Equals("Completed", StringComparison.OrdinalIgnoreCase) != true)
        {
            await WaitForSearchCompletionAsync(
                    client,
                    repo.RepositoryId,
                    token,
                    displayQuery,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await FetchSearchResultsAsync(
                client,
                repo.RepositoryId,
                token,
                page,
                pageSize,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WaitForSearchCompletionAsync(
        HttpClient client,
        string repositoryId,
        string operationToken,
        string displayQuery,
        CancellationToken cancellationToken)
    {
        var statusUrl = _adapter.BuildTaskStatusUrl(repositoryId, operationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(MaxPollDurationSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            using var statusResponse = await client.GetAsync(statusUrl, cancellationToken).ConfigureAwait(false);
            var statusBody = await statusResponse.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!statusResponse.IsSuccessStatusCode)
            {
                throw new LaserficheException(
                    $"Search status endpoint returned HTTP {(int)statusResponse.StatusCode} for {statusUrl}. " +
                    $"Body: {statusBody}",
                    (int)statusResponse.StatusCode);
            }

            var status = ParseOperationStatus(statusBody);

            if (status.Status?.Equals("Completed", StringComparison.OrdinalIgnoreCase) == true)
                return;

            if (status.Status?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new LaserficheException(
                    $"Laserfiche search operation failed for query: {displayQuery}. " +
                    $"Errors: {string.Join("; ", status.Errors)}",
                    500);
            }
        }

        throw new TimeoutException(
            $"Laserfiche search timed out after {MaxPollDurationSeconds} seconds. " +
            "Try a more specific query.");
    }

    private async Task<PagedResult<LFSearchResult>> FetchSearchResultsAsync(
        HttpClient client,
        string repositoryId,
        string operationToken,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var firstUrl = AddPagingQuery(
            _adapter.BuildSearchResultsUrl(repositoryId, operationToken), page, pageSize);
        client.DefaultRequestHeaders.Remove("Prefer");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={pageSize}");
        return await ReadRequestedPageAsync(client, null, firstUrl, 1, pageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PagedResult<LFSearchResult>> ReadRequestedPageAsync(
        HttpClient client,
        string? initialBody,
        string initialUrl,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var skip = checked((page - 1) * pageSize);
        var required = checked(skip + pageSize);
        var received = new List<LFSearchResult>(Math.Min(required, 512));
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextUrl = initialUrl;
        string? body = initialBody;
        int? totalCount = null;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!visitedUrls.Add(nextUrl))
            {
                throw new LaserficheException(
                    $"Search-result pagination repeated a nextLink: {nextUrl}",
                    500);
            }

            if (body is null)
            {
                using var response = await client.GetAsync(nextUrl, cancellationToken).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new LaserficheException(
                        $"Search results returned HTTP {(int)response.StatusCode} at {nextUrl}. Body: {body}",
                        (int)response.StatusCode);
                }
            }

            var parsed = ParseResultPage(body);
            received.AddRange(parsed.Items.Select(MapSearchResult));
            totalCount ??= parsed.TotalCount;
            nextUrl = ResolveNextLink(nextUrl, parsed.NextLink);
            body = null;

            _logger.LogInformation(
                "Search result chunk: {PageCount} item(s), buffered={Buffered}, total={Total}, nextLink={HasNext}.",
                parsed.Items.Count, received.Count, totalCount, nextUrl is null ? "no" : "yes");

            if (received.Count >= required || nextUrl is null)
                break;
        }

        var distinct = received
            .GroupBy(r => r.EntryId)
            .Select(g => g
                .OrderByDescending(r => r.LastModifiedTime ?? r.CreationTime ?? DateTimeOffset.MinValue)
                .First())
            .ToList();

        var items = distinct.Skip(skip).Take(pageSize).ToList().AsReadOnly();
        var effectiveTotal = totalCount ?? (nextUrl is null ? distinct.Count : skip + items.Count + 1);
        return new PagedResult<LFSearchResult>
        {
            Items = items,
            TotalCount = Math.Max(effectiveTotal, skip + items.Count),
            PageNumber = page,
            PageSize = pageSize
        };
    }

    private static ResultPage ParseResultPage(string body)
    {
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new JsonException("Search result response body was empty.");

        if (body.StartsWith('['))
        {
            var items = JsonSerializer.Deserialize<List<SearchResultResource>>(body, JsonOptions.Default) ?? [];
            return new ResultPage(items, null, items.Count);
        }

        var result = JsonSerializer.Deserialize<ODataPagedList<SearchResultResource>>(body, JsonOptions.Default)
            ?? throw new JsonException("Search result response could not be deserialized.");

        return new ResultPage(
            result.Value,
            result.NextLink ?? result.PlainNextLink,
            result.TotalCount ?? result.PlainCount ?? result.Count);
    }

    private static LongOperationResponse ParseOperationStatus(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(root, "value", out var value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            var first = value.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined)
                throw new JsonException("Search status response contained no task.");
            return first.Deserialize<LongOperationResponse>(JsonOptions.Default)
                ?? throw new JsonException("Search status task could not be deserialized.");
        }

        return root.Deserialize<LongOperationResponse>(JsonOptions.Default)
            ?? throw new JsonException("Search status response could not be deserialized.");
    }

    private static LFSearchResult MapSearchResult(SearchResultResource r) => new()
    {
        EntryId = r.Id,
        Name = r.Name,
        FullPath = r.FullPath,
        EntryType = ParseEntryType(r.EntryType ?? r.ODataType),
        TemplateName = r.TemplateName,
        TemplateId = r.TemplateId,
        Creator = r.Creator,
        CreationTime = r.CreationTime,
        LastModifiedTime = r.LastModifiedTime
    };

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
            throw new JsonException($"Search results nextLink points outside the active Laserfiche API host: {nextLink}");
        }

        return resolved.AbsoluteUri;
    }

    private static void ValidatePaging(int page, int pageSize)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be at least 1.");
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be at least 1.");
    }

    private static string AddPagingQuery(string url, int page, int pageSize)
    {
        var skip = checked((page - 1) * pageSize);
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}$skip={skip}&$top={pageSize}&$count=true&$orderby=creationTime%20desc";
    }

    private static string EscapeSearchTerm(string term) =>
        term.Replace("\"", "\\\"").Replace("\\", "\\\\");

    private static LFEntryType ParseEntryType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return LFEntryType.Unknown;

        var token = raw.TrimStart('#');
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

    private sealed record LongOperationResponse
    {
        [JsonPropertyName("operationToken")]
        public string? OperationToken { get; init; }

        [JsonPropertyName("taskId")]
        public string? TaskId { get; init; }

        [JsonPropertyName("token")]
        public string? Token { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("percentComplete")]
        public int PercentComplete { get; init; }

        [JsonPropertyName("errors")]
        public List<string> Errors { get; init; } = [];
    }

    private sealed record ResultPage(List<SearchResultResource> Items, string? NextLink, int? TotalCount);

    private sealed record ODataPagedList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }

        [JsonPropertyName("nextLink")]
        public string? PlainNextLink { get; init; }

        [JsonPropertyName("@odata.count")]
        public int? TotalCount { get; init; }

        [JsonPropertyName("totalCount")]
        public int? PlainCount { get; init; }

        [JsonPropertyName("count")]
        public int? Count { get; init; }
    }

    private sealed record SearchResultResource
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("fullPath")]
        public string FullPath { get; init; } = string.Empty;

        [JsonPropertyName("entryType")]
        public string? EntryType { get; init; }

        [JsonPropertyName("@odata.type")]
        public string? ODataType { get; init; }

        [JsonPropertyName("templateName")]
        public string? TemplateName { get; init; }

        [JsonPropertyName("templateId")]
        public int? TemplateId { get; init; }

        [JsonPropertyName("creator")]
        public string? Creator { get; init; }

        [JsonPropertyName("creationTime")]
        public DateTimeOffset? CreationTime { get; init; }

        [JsonPropertyName("lastModifiedTime")]
        public DateTimeOffset? LastModifiedTime { get; init; }
    }
}
