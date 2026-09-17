using System.Text.Json;
using System.Text.Json.Serialization;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Implements repository-level operations by calling the Laserfiche Repository API v1
/// <c>GET /Repositories</c> endpoint. All data returned by this service is sourced directly
/// from the live API with no local caching.
/// </summary>
internal sealed class LaserficheRepositoryService : ILaserficheRepositoryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryContext _repositoryContext;
    private readonly ILaserficheApiAdapter _adapter;
    private readonly ILogger<LaserficheRepositoryService> _logger;

    /// <summary>Initialises the service with all required dependencies.</summary>
    public LaserficheRepositoryService(
        IHttpClientFactory httpClientFactory,
        IRepositoryContext repositoryContext,
        ILaserficheApiAdapter adapter,
        ILogger<LaserficheRepositoryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repositoryContext = repositoryContext;
        _adapter = adapter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(
        string serverUrl,
        string repositoryId,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Discovering repositories on server {ServerUrl}.", serverUrl);

        using var client = _httpClientFactory.CreateClient("LaserficheRaw");
        var token = await RequestTokenWithCredentialsAsync(
            client,
            _adapter.BuildTokenUrlFor(serverUrl, repositoryId),
            username,
            password,
            cancellationToken).ConfigureAwait(false);

        var url = _adapter.BuildRepositoriesUrlFor(serverUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadRepositoryJsonAsync(response, url, cancellationToken)
            .ConfigureAwait(false);
        var repositories = DeserializeRepositories(body, url);

        return repositories
            .Select(r => ToRepositoryInfo(r))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<RepositoryInfo> GetRepositoryInfoAsync(
        CancellationToken cancellationToken = default)
    {
        var repo = await _repositoryContext
            .GetActiveRepositoryAsync(cancellationToken)
            .ConfigureAwait(false);

        var url = _adapter.BuildRepositoriesUrl();

        _logger.LogInformation(
            "Checking configured repository {RepositoryId} using documented GET /Repositories: {Url}.",
            repo.RepositoryId,
            url);

        using var client = _httpClientFactory.CreateClient("LaserficheAuthenticated");

        // Use SendAsync so we can inspect response headers for server version info
        // before consuming the response body.
        using var response = await client
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        var serverVersion = TryExtractServerVersion(response);

        var body = await ReadRepositoryJsonAsync(response, url, cancellationToken)
            .ConfigureAwait(false);

        var repositories = DeserializeRepositories(body, url);
        var match        = FindConfiguredRepository(repositories, repo.RepositoryId, url);

        return ToRepositoryInfo(match, serverVersion);
    }

    /// <inheritdoc />
    public async Task<ConnectionStatus> TestConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await GetRepositoryInfoAsync(cancellationToken).ConfigureAwait(false);
            return ConnectionStatus.Success(info);
        }
        catch (LaserficheException lex) when (lex.StatusCode == 200)
        {
            // The server responded with HTTP 200 but the repository-list body had an
            // unrecognised JSON shape. This means the server IS reachable and the
            // Bearer token WAS accepted — only discovery is limited. Report as
            // "connected" with a note rather than labelling the connection "Disconnected".
            //
            // Under normal circumstances this branch should not be reached once the
            // parser recognises the v1 plain-array and v2 OData shapes. It remains as
            // a safety-net for future API response changes.
            _logger.LogWarning(
                "[CONNECTIVITY] Repository discovery returned HTTP 200 with an unrecognised " +
                "body shape. Server is reachable and auth succeeded; only the repository list " +
                "format was not understood. Discovery limitation: {Error}", lex.Message);

            var repo = await _repositoryContext
                .GetActiveRepositoryAsync(cancellationToken).ConfigureAwait(false);

            return ConnectionStatus.Success(new RepositoryInfo
            {
                RepositoryId   = repo.RepositoryId,
                RepositoryName = repo.RepositoryId,
                ServerVersion  = $"Laserfiche API {_adapter.ApiVersion} (discovery limited)",
                ApiVersion     = _adapter.ApiVersion,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed.");
            return ConnectionStatus.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionStatus> TestConnectionWithCredentialsAsync(
        string serverUrl,
        string repositoryId,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Use the serverUrl supplied by the caller — NOT the stored config —
            // so the test hits exactly what the user typed into the Settings form.
            var tokenUrl = _adapter.BuildTokenUrlFor(serverUrl, repositoryId);
            var repositoriesUrl = _adapter.BuildRepositoriesUrlFor(serverUrl);

            _logger.LogInformation(
                "Test connection → POST {TokenUrl}", tokenUrl);

            using var client = _httpClientFactory.CreateClient("LaserficheRaw");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"]   = username,
                ["password"]   = password
            });

            using var tokenResponse = await client
                .PostAsync(tokenUrl, form, cancellationToken)
                .ConfigureAwait(false);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                var tokenBody404 = await tokenResponse.Content
                    .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Token request failed: HTTP {Status} from {Url}. Body: {Body}",
                    (int)tokenResponse.StatusCode, tokenUrl, tokenBody404);

                return ConnectionStatus.Failure(
                    $"Authentication failed: HTTP {(int)tokenResponse.StatusCode}. " +
                    $"URL attempted: {tokenUrl}. Check the Server URL, API version, and credentials.");
            }

            var tokenBody = await tokenResponse.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(tokenBody);
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
            {
                return ConnectionStatus.Failure("Authentication succeeded but no token was returned.");
            }

            var token = tokenEl.GetString() ?? string.Empty;

            _logger.LogInformation(
                "Authentication succeeded. Test connection → GET documented repository list {RepositoriesUrl}.",
                repositoriesUrl);

            using var repoRequest = new HttpRequestMessage(HttpMethod.Get, repositoriesUrl);
            repoRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var repoResponse = await client
                .SendAsync(repoRequest, cancellationToken)
                .ConfigureAwait(false);

            var body = await ReadRepositoryJsonAsync(
                repoResponse, repositoriesUrl, cancellationToken).ConfigureAwait(false);
            var repositories = DeserializeRepositories(body, repositoriesUrl);
            var match = FindConfiguredRepository(repositories, repositoryId, repositoriesUrl);

            _logger.LogInformation(
                "Authentication successful and repository {RepositoryId} found.",
                match.RepoId);

            return ConnectionStatus.Success(ToRepositoryInfo(match));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test with explicit credentials failed.");
            return ConnectionStatus.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Throws a <see cref="LaserficheException"/> if the HTTP response indicates an error,
    /// including the response body in the exception message for diagnostics.
    /// </summary>
    private async Task<string> GetRepositoryJsonAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await client
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        return await ReadRepositoryJsonAsync(response, url, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> RequestTokenWithCredentialsAsync(
        HttpClient client,
        string tokenUrl,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password
        });

        using var response = await client
            .PostAsync(tokenUrl, form, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LaserficheException(
                $"Authentication failed with HTTP {(int)response.StatusCode}. " +
                $"URL attempted: {tokenUrl}. Response body: {body}",
                (int)response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement) ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new LaserficheException(
                $"Authentication succeeded but no access_token was returned by {tokenUrl}. " +
                $"Response body: {body}",
                (int)response.StatusCode);
        }

        return tokenElement.GetString()!;
    }

    private async Task<string> ReadRepositoryJsonAsync(
        HttpResponseMessage response,
        string url,
        CancellationToken cancellationToken)
    {
        var body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "RAW Repository JSON:\n{json}",
            body);

        if (!response.IsSuccessStatusCode)
        {
            throw new LaserficheException(
                $"Laserfiche API returned HTTP {(int)response.StatusCode} for documented " +
                $"GET /Repositories at {url}. Body: {body}",
                (int)response.StatusCode);
        }

        return body;
    }

    /// <summary>
    /// Parses the repository list from the API response body.
    /// Handles both the V1 plain-array <c>[{...}]</c> and V2 OData envelope
    /// <c>{"value":[...]}</c> shapes so callers remain version-agnostic.
    /// </summary>
    private List<RepositoryDto> DeserializeRepositories(string body, string url)
    {
        var repositories = RepositoryJsonParser.TryParse(body, out var shape);

        _logger.LogInformation(
            "[REPO DISCOVERY] Response shape: {Shape} from {Url}", shape, url);

        if (repositories is null)
        {
            throw new LaserficheException(
                $"Laserfiche repository response was not a JSON array matching RepositoryDto. " +
                $"URL: {url}. Detected shape: {shape}. " +
                $"Body (first 200 chars): {(body.Length > 200 ? body[..200] + "\u2026" : body)}",
                200);
        }

        return repositories;
    }

    private static RepositoryDto FindConfiguredRepository(
        IReadOnlyList<RepositoryDto> repositories,
        string configuredRepositoryId,
        string url)
    {
        var match = repositories.FirstOrDefault(r =>
            string.Equals(
                r.RepoId,
                configuredRepositoryId,
                StringComparison.OrdinalIgnoreCase));

        return match ?? throw new LaserficheException(
            $"Authenticated successfully, but repository '{configuredRepositoryId}' " +
            $"was not found in the repository list returned by {url}.",
            404);
    }

    private RepositoryInfo ToRepositoryInfo(RepositoryDto repository, string? serverVersion = null) =>
        new()
        {
            RepositoryId   = repository.RepoId,
            RepositoryName = repository.RepoName,
            // Use any version found in HTTP response headers; fall back to a descriptive label
            // that at least tells the user which API version is in use.
            ServerVersion  = serverVersion ?? $"Laserfiche API {_adapter.ApiVersion}",
            ApiVersion     = _adapter.ApiVersion
        };

    /// <summary>
    /// Inspects HTTP response headers returned by the Laserfiche API server and extracts
    /// any version identifier present. Returns <c>null</c> when no version header is found.
    /// </summary>
    /// <remarks>
    /// Laserfiche API installations may expose version information via various header names
    /// (<c>api-version</c>, <c>x-api-version</c>, <c>x-laserfiche-api-version</c>,
    /// <c>x-server-version</c>). We also check the <c>Server</c> and <c>X-Powered-By</c>
    /// response headers as a last resort. The first non-empty value found is returned.
    /// </remarks>
    private static string? TryExtractServerVersion(HttpResponseMessage response)
    {
        // Ordered list of header names to probe — most-specific first.
        ReadOnlySpan<string> candidates =
        [
            "x-server-version",
            "x-laserfiche-api-version",
            "x-api-version",
            "api-version",
            "x-powered-by",
            "server"
        ];

        foreach (var header in candidates)
        {
            if (response.Headers.TryGetValues(header, out var values))
            {
                var value = values.FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
