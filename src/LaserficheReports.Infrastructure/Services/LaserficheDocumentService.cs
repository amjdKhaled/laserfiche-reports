using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Implements document retrieval operations against the active Laserfiche Repository API.
/// Collection endpoints are read to completion; failed source requests are surfaced rather
/// than converted to empty data.
/// </summary>
internal sealed class LaserficheDocumentService : ILaserficheDocumentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILaserficheEntryService _entryService;
    private readonly ILaserficheApiAdapter _adapter;
    private readonly ILogger<LaserficheDocumentService> _logger;

    public LaserficheDocumentService(
        IHttpClientFactory httpClientFactory,
        IRepositoryContext repositoryContext,
        ILaserficheEntryService entryService,
        ILaserficheApiAdapter adapter,
        ILogger<LaserficheDocumentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repositoryContext = repositoryContext;
        _entryService = entryService;
        _adapter = adapter;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LFDocumentPage>> GetDocumentPagesAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var firstUrl = _adapter.BuildEntryUrl(repo.RepositoryId, entryId, EntryResource.Pages);
        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        var pages = new List<PageResource>();
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextUrl = firstUrl;
        var apiPage = 0;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!visitedUrls.Add(nextUrl))
            {
                throw new LaserficheException(
                    $"Document pages pagination repeated a nextLink for entry {entryId}: {nextUrl}",
                    500);
            }

            apiPage++;
            using var response = await client.GetAsync(nextUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LaserficheException(
                    $"Document pages request failed for entry {entryId}: HTTP {(int)response.StatusCode}. " +
                    $"URL: {nextUrl}. Body: {body}",
                    (int)response.StatusCode);
            }

            var parsed = ParsePage(body);
            pages.AddRange(parsed.Items);
            nextUrl = ResolveNextLink(nextUrl, parsed.NextLink);

            _logger.LogInformation(
                "Document pages. EntryId={EntryId}; ApiPage={ApiPage}; Items={Items}; RunningTotal={Total}; HasNext={HasNext}.",
                entryId, apiPage, parsed.Items.Count, pages.Count, nextUrl is not null);
        }

        return pages
            .Where(p => p.PageNumber > 0)
            .GroupBy(p => p.PageNumber)
            .Select(g => g.Last())
            .OrderBy(p => p.PageNumber)
            .Select(p => new LFDocumentPage
            {
                PageNumber = p.PageNumber,
                Width = p.Width,
                Height = p.Height,
                MimeType = p.MimeType
            })
            .ToList()
            .AsReadOnly();
    }

    public async Task<LaserficheEdocStream> StreamEdocAsync(
        int entryId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var url = _adapter.BuildEntryUrl(repo.RepositoryId, entryId, EntryResource.Edoc);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        // Repository API V2 retrieves an electronic document through Simple Export.
        // The /Document/Edoc resource is used for mutation, while Export is the
        // documented browser-safe retrieval flow and returns a short-lived link.
        if (_adapter.ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase))
        {
            return await ExportElectronicDocumentAsync(
                    client, repo.RepositoryId, entryId, cancellationToken)
                .ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            throw new LaserficheException(
                $"Electronic document request failed for entry {entryId}: HTTP {statusCode}. Body: {body}",
                statusCode);
        }

        try
        {
            var contentStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var contentDisposition = response.Content.Headers.ContentDisposition?.ToString();
            var fileName = GetFileName(response.Content.Headers.ContentDisposition);
            var contentType = NormalizeContentType(
                response.Content.Headers.ContentType?.MediaType,
                fileName);
            var extension = GetExtension(fileName, contentType);

            return new LaserficheEdocStream(
                contentStream,
                contentType,
                contentDisposition,
                fileName,
                extension,
                response.Content.Headers.ContentLength,
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<LaserficheEdocStream> GetPageImageAsync(
        int entryId,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be at least 1.");

        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var url = _adapter.BuildPageImageUrl(repo.RepositoryId, entryId, pageNumber);

        var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");
        var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            response.Dispose();

            // Some on-premises Repository API v1 installations do not expose
            // /pages/{pageNumber}/image. Their supported read endpoint is the
            // document edoc resource instead. Preserve the page-image route for
            // servers that support it, then fall back only when v1 returns 404.
            if (statusCode == (int)System.Net.HttpStatusCode.NotFound &&
                _adapter.ApiVersion.Equals("v1", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Laserfiche v1 page image route was not found for entry {EntryId} page {PageNumber}; falling back to the edoc resource.",
                    entryId,
                    pageNumber);

                return await StreamEdocAsync(entryId, cancellationToken).ConfigureAwait(false);
            }

            throw new LaserficheException(
                $"Page image not available for entry {entryId} page {pageNumber}: " +
                $"HTTP {statusCode}. Body: {body}",
                statusCode);
        }

        var directFileName = GetFileName(response.Content.Headers.ContentDisposition);
        var directContentType = NormalizeContentType(
            response.Content.Headers.ContentType?.MediaType,
            directFileName);

        // Laserfiche image pages are commonly stored as TIFF. Browsers do not
        // consistently render TIFF, so V2 asks Laserfiche to export this one page
        // as PNG while preserving the original repository document unchanged.
        if (_adapter.ApiVersion.Equals("v2", StringComparison.OrdinalIgnoreCase) &&
            (directContentType.Equals("image/tiff", StringComparison.OrdinalIgnoreCase) ||
             directContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
        {
            response.Dispose();
            return await ExportPageAsPngAsync(
                    client, repo.RepositoryId, entryId, pageNumber, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var contentStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            // Do not invent an image type when the server omits Content-Type.
            var fileName = directFileName;
            var contentType = directContentType;

            return new LaserficheEdocStream(
                contentStream,
                contentType,
                contentDisposition: response.Content.Headers.ContentDisposition?.ToString(),
                fileName: fileName,
                extension: GetExtension(fileName, contentType),
                contentLength: response.Content.Headers.ContentLength,
                owner: response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public Task<LFEntry> GetDocumentMetadataAsync(
        int entryId,
        CancellationToken cancellationToken = default) =>
        _entryService.GetEntryAsync(entryId, cancellationToken);

    private async Task<LaserficheEdocStream> ExportPageAsPngAsync(
        HttpClient client,
        string repositoryId,
        int entryId,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        var exportUrl = _adapter.BuildDocumentExportUrl(
            repositoryId, entryId, pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        const string requestJson =
            "{\"part\":\"Image\",\"imageOptions\":{\"format\":\"PNG\",\"includeAnnotations\":true,\"includeRedactions\":true}}";

        using var request = new HttpRequestMessage(HttpMethod.Post, exportUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        using var exportResponse = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var exportBody = await exportResponse.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!exportResponse.IsSuccessStatusCode)
        {
            throw new LaserficheException(
                $"PNG page export failed for entry {entryId} page {pageNumber}: " +
                $"HTTP {(int)exportResponse.StatusCode}. Body: {exportBody}",
                (int)exportResponse.StatusCode);
        }

        var downloadLink = ParseExportDownloadLink(exportBody);
        var downloadUrl = ResolveTrustedExportLink(exportUrl, downloadLink);
        var downloadResponse = await client
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!downloadResponse.IsSuccessStatusCode)
        {
            var body = await downloadResponse.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var statusCode = (int)downloadResponse.StatusCode;
            downloadResponse.Dispose();
            throw new LaserficheException(
                $"PNG page download failed for entry {entryId} page {pageNumber}: " +
                $"HTTP {statusCode}. Body: {body}",
                statusCode);
        }

        try
        {
            var stream = await downloadResponse.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var fileName = GetFileName(downloadResponse.Content.Headers.ContentDisposition)
                ?? $"page-{pageNumber}.png";
            var contentType = NormalizeContentType(
                downloadResponse.Content.Headers.ContentType?.MediaType,
                fileName);

            return new LaserficheEdocStream(
                stream,
                contentType == "application/octet-stream" ? "image/png" : contentType,
                downloadResponse.Content.Headers.ContentDisposition?.ToString(),
                fileName,
                ".png",
                downloadResponse.Content.Headers.ContentLength,
                downloadResponse);
        }
        catch
        {
            downloadResponse.Dispose();
            throw;
        }
    }

    private async Task<LaserficheEdocStream> ExportElectronicDocumentAsync(
        HttpClient client,
        string repositoryId,
        int entryId,
        CancellationToken cancellationToken)
    {
        var exportUrl = _adapter.BuildDocumentExportUrl(repositoryId, entryId);
        using var request = new HttpRequestMessage(HttpMethod.Post, exportUrl)
        {
            Content = new StringContent("{\"part\":\"Edoc\"}", Encoding.UTF8, "application/json")
        };
        using var exportResponse = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var exportBody = await exportResponse.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!exportResponse.IsSuccessStatusCode)
        {
            throw new LaserficheException(
                $"Electronic document export failed for entry {entryId}: " +
                $"HTTP {(int)exportResponse.StatusCode}. Body: {exportBody}",
                (int)exportResponse.StatusCode);
        }

        var downloadLink = ParseExportDownloadLink(exportBody);
        var downloadUrl = ResolveTrustedExportLink(exportUrl, downloadLink);
        var downloadResponse = await client
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!downloadResponse.IsSuccessStatusCode)
        {
            var body = await downloadResponse.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var statusCode = (int)downloadResponse.StatusCode;
            downloadResponse.Dispose();
            throw new LaserficheException(
                $"Electronic document download failed for entry {entryId}: " +
                $"HTTP {statusCode}. Body: {body}",
                statusCode);
        }

        try
        {
            var stream = await downloadResponse.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var fileName = GetFileName(downloadResponse.Content.Headers.ContentDisposition);
            var contentType = NormalizeContentType(
                downloadResponse.Content.Headers.ContentType?.MediaType,
                fileName);

            return new LaserficheEdocStream(
                stream,
                contentType,
                downloadResponse.Content.Headers.ContentDisposition?.ToString(),
                fileName,
                GetExtension(fileName, contentType),
                downloadResponse.Content.Headers.ContentLength,
                downloadResponse);
        }
        catch
        {
            downloadResponse.Dispose();
            throw;
        }
    }

    internal static string ParseExportDownloadLink(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString() ?? throw new JsonException("Export download link was empty.");

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new JsonException("Export response did not contain a download link.");
    }

    private static string ResolveTrustedExportLink(string exportUrl, string downloadLink)
    {
        if (!Uri.TryCreate(exportUrl, UriKind.Absolute, out var source) ||
            !Uri.TryCreate(source, downloadLink, out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(source.Scheme, resolved.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Authority, resolved.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("Laserfiche export returned an untrusted download link.");
        }

        return resolved.AbsoluteUri;
    }

    private static PageList ParsePage(string body)
    {
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new JsonException("Document pages response body was empty.");

        if (body.StartsWith('['))
        {
            var items = JsonSerializer.Deserialize<List<PageResource>>(body, JsonOptions.Default) ?? [];
            return new PageList(items, null);
        }

        var result = JsonSerializer.Deserialize<ODataList<PageResource>>(body, JsonOptions.Default)
            ?? throw new JsonException("Document pages response could not be deserialized.");

        return new PageList(result.Value, result.NextLink ?? result.PlainNextLink);
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
            throw new JsonException($"Document pages nextLink points outside the active Laserfiche API host: {nextLink}");
        }

        return resolved.AbsoluteUri;
    }

    private static string? GetFileName(ContentDispositionHeaderValue? disposition)
    {
        var value = disposition?.FileNameStar ?? disposition?.FileName;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Trim('"');
    }

    private static string? GetExtension(string? fileName, string contentType)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var extension = Path.GetExtension(fileName);
            if (!string.IsNullOrWhiteSpace(extension)) return extension;
        }

        return contentType.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => null
        };
    }

    internal static string NormalizeContentType(string? contentType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = contentType.Trim().ToLowerInvariant();
            return normalized == "image/jpg" ? "image/jpeg" : normalized;
        }

        return Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
    }

    private sealed record PageList(List<PageResource> Items, string? NextLink);

    private sealed record ODataList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }

        [JsonPropertyName("nextLink")]
        public string? PlainNextLink { get; init; }
    }

    private sealed record PageResource
    {
        [JsonPropertyName("pageNumber")]
        public int PageNumber { get; init; }

        [JsonPropertyName("width")]
        public int? Width { get; init; }

        [JsonPropertyName("height")]
        public int? Height { get; init; }

        [JsonPropertyName("mimeType")]
        public string? MimeType { get; init; }
    }
}
