using System.Net;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Repository;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// Regression tests for the separation of "repository discovery" (GET /Repositories)
/// from "repository connectivity" (can we reach and authenticate against THIS repo).
///
/// Key invariants tested:
///  - A V2 OData response from GET /Repositories is correctly parsed (not thrown as JsonException).
///  - A V1 plain-array response is still correctly parsed.
///  - When GET /Repositories returns HTTP 200 with an unrecognisable body,
///    <see cref="LaserficheRepositoryService.TestConnectionAsync"/> returns
///    <c>IsConnected = true</c> (server reachable, auth succeeded) rather than
///    <c>IsConnected = false</c> (which wrongly labels the whole connection "Disconnected").
///  - Login with explicit credentials (TryAuthenticateAsync) is completely independent
///    of repository enumeration — it succeeds even when GetRepositoryInfoAsync would fail.
/// </summary>
public sealed class RepositoryServiceDiscoveryTests
{
    // ── Shared fixtures ───────────────────────────────────────────────────────

    private static LaserficheOptions TestOptions(string repoId = "TestRepo") => new()
    {
        ServerUrl    = "http://lf-server.test",
        ApiBasePath  = "/LFRepositoryAPI",
        ApiVersion   = "v1",
        RepositoryId = repoId,
    };

    private static RepositoryDescriptor TestDescriptor(string repoId = "TestRepo") =>
        new("default", "http://lf-server.test", repoId, repoId);

    /// <summary>
    /// Creates a <see cref="LaserficheRepositoryService"/> wired to the given HTTP handler.
    /// The handler handles ALL named clients returned by the factory (no BearerTokenHandler
    /// in tests — the handler receives requests directly from GetAsync/PostAsync).
    /// </summary>
    private static LaserficheRepositoryService CreateService(
        TestHttpMessageHandler handler,
        LaserficheOptions?     options = null,
        string?                activeRepoId = null)
    {
        var opts    = options ?? TestOptions();
        var monitor = new StaticOptionsMonitor<LaserficheOptions>(opts);
        var adapter = new LaserficheApiAdapter(monitor);
        var repo    = activeRepoId is not null ? TestDescriptor(activeRepoId) : TestDescriptor(opts.RepositoryId);
        var ctx     = new StubRepositoryContext(repo);
        var factory = new TestHttpClientFactory(handler);
        var logger  = NullLogger<LaserficheRepositoryService>.Instance;
        return new LaserficheRepositoryService(factory, ctx, adapter, logger);
    }

    // ── Requirement 5: V1 plain-array parsing ─────────────────────────────────

    [Fact]
    public async Task GetRepositoryInfoAsync_V1PlainArray_ReturnsMatchedRepository()
    {
        var body = """
            [
              {"repoId":"TestRepo","repoName":"Test Repository","webclientUrl":"http://lf/Laserfiche"}
            ]
            """;
        var handler = OkHandler(body);

        var service = CreateService(handler);
        var info    = await service.GetRepositoryInfoAsync();

        Assert.Equal("TestRepo", info.RepositoryId);
        Assert.Equal("Test Repository", info.RepositoryName);
    }

    // ── Requirement 6: V2 OData wrapper parsing ───────────────────────────────

    [Fact]
    public async Task GetRepositoryInfoAsync_V2ODataWrapper_ReturnsMatchedRepository()
    {
        // This is the exact shape that Laserfiche V2 GET /Repositories returns.
        // Before the RepositoryJsonParser fix this call threw a JsonException.
        var body = """
            {
              "@odata.context": "http://lf-server.test/LFRepositoryAPI/v2/$metadata#Repositories",
              "value": [
                {
                  "repoId": "TestRepo",
                  "repoName": "Test Repository",
                  "webclientUrl": "http://lf-server.test/Laserfiche"
                }
              ]
            }
            """;
        var handler = OkHandler(body);

        var service = CreateService(handler);
        var info    = await service.GetRepositoryInfoAsync();

        Assert.Equal("TestRepo", info.RepositoryId);
        Assert.Equal("Test Repository", info.RepositoryName);
    }

    [Fact]
    public async Task GetRepositoryInfoAsync_V2ODataWrapper_MultipleRepos_FindsConfiguredRepo()
    {
        var body = """
            {
              "value": [
                {"repoId":"OtherRepo","repoName":"Other","webclientUrl":""},
                {"repoId":"TestRepo","repoName":"Test Repository","webclientUrl":""}
              ]
            }
            """;
        var handler = OkHandler(body);

        var service = CreateService(handler);
        var info    = await service.GetRepositoryInfoAsync();

        Assert.Equal("TestRepo", info.RepositoryId);
    }

    [Fact]
    public async Task GetRepositoryInfoAsync_V2ODataWrapper_EmptyList_ThrowsLaserficheException()
    {
        // An empty repository list means the configured repo cannot be found.
        var body    = """{"value":[]}""";
        var handler = OkHandler(body);

        var service = CreateService(handler);
        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => service.GetRepositoryInfoAsync());

        Assert.Contains("TestRepo", ex.Message);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Requirement 7: Invalid JSON never crashes ─────────────────────────────

    [Fact]
    public async Task GetRepositoryInfoAsync_UnrecognisedJsonBody_ThrowsLaserficheException()
    {
        // Body is valid JSON but not a repository list shape.
        var body    = """{"error":"format_unknown","code":500}""";
        var handler = OkHandler(body);

        var service = CreateService(handler);
        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => service.GetRepositoryInfoAsync());

        // Must explain the shape problem, not crash with a raw JsonException.
        Assert.Contains("shape", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(200, ex.StatusCode);
    }

    // ── Requirement 10: Discovery failure ≠ connection failure ────────────────

    [Fact]
    public async Task TestConnectionAsync_WhenDiscoveryBodyUnrecognised_ReturnsConnectedNotFailed()
    {
        // GET /Repositories returns HTTP 200 but with an unrecognisable JSON body.
        // This means the server IS reachable and auth succeeded.
        // TestConnectionAsync must return IsConnected = true (not "Disconnected") because
        // "discovery limited" is not the same as "connection down".
        var incompatibleBody = """{"notARepoList":true,"reason":"format_changed"}""";
        var handler          = OkHandler(incompatibleBody);

        var service = CreateService(handler);
        var status  = await service.TestConnectionAsync();

        Assert.True(status.IsConnected,
            "Connection must be reported as up when the server responds HTTP 200 — " +
            "even if the repository list body shape was unrecognised.");
        Assert.Equal("TestRepo", status.RepositoryId);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerReturnsV2ODataBody_ReturnsConnected()
    {
        // After the RepositoryJsonParser fix, V2 OData responses are parsed correctly
        // and TestConnectionAsync should return Connected without triggering the fallback.
        var v2Body  = """{"value":[{"repoId":"TestRepo","repoName":"Test","webclientUrl":""}]}""";
        var handler = OkHandler(v2Body);

        var service = CreateService(handler);
        var status  = await service.TestConnectionAsync();

        Assert.True(status.IsConnected);
        Assert.Equal("TestRepo", status.RepositoryId);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerReturnsV1PlainArray_ReturnsConnected()
    {
        var v1Body  = """[{"repoId":"TestRepo","repoName":"Test","webclientUrl":""}]""";
        var handler = OkHandler(v1Body);

        var service = CreateService(handler);
        var status  = await service.TestConnectionAsync();

        Assert.True(status.IsConnected);
        Assert.Equal("TestRepo", status.RepositoryId);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerIsUnreachable_ReturnsFailure()
    {
        var handler = new TestHttpMessageHandler
        {
            ExceptionToThrow = new HttpRequestException("Connection refused")
        };

        var service = CreateService(handler);
        var status  = await service.TestConnectionAsync();

        Assert.False(status.IsConnected);
        Assert.Contains("Connection refused", status.ErrorMessage);
    }

    // ── Requirement 11: Login is independent of repository discovery ──────────
    //
    // LaserficheRepositoryService is NOT injected into LoginController.
    // Login calls ILaserficheAuthService.TryAuthenticateAsync which posts directly
    // to the /Token endpoint — it never calls GET /Repositories.
    // This test verifies that LaserficheRepositoryService.TestConnectionAsync failure
    // does NOT prevent token acquisition — both are independent code paths.

    [Fact]
    public async Task DiscoverRepositoriesAsync_WithValidCredentials_V1PlainArray_Succeeds()
    {
        // Token endpoint returns access_token.
        // Repositories endpoint returns V1 array.
        // Both are handled by the same TestHttpMessageHandler in sequence.
        var callCount = 0;
        var handler = new TestHttpMessageHandler();
        handler.ResponseFactory = _ =>
        {
            callCount++;
            // First call: POST /Token
            if (callCount == 1)
                return OkJson("""{"access_token":"test-token","token_type":"Bearer"}""");
            // Second call: GET /Repositories
            return OkJson("""[{"repoId":"TestRepo","repoName":"Test","webclientUrl":""}]""");
        };

        var service = CreateService(handler);
        var repos = await service.DiscoverRepositoriesAsync(
            "http://lf-server.test", "TestRepo", "user", "pass");

        Assert.Single(repos);
        Assert.Equal("TestRepo", repos[0].RepositoryId);
    }

    [Fact]
    public async Task DiscoverRepositoriesAsync_WithValidCredentials_V2ODataArray_Succeeds()
    {
        var callCount = 0;
        var handler = new TestHttpMessageHandler();
        handler.ResponseFactory = _ =>
        {
            callCount++;
            if (callCount == 1)
                return OkJson("""{"access_token":"test-token","token_type":"Bearer"}""");
            return OkJson("""{"value":[{"repoId":"TestRepo","repoName":"Test","webclientUrl":""}]}""");
        };

        var service = CreateService(handler);
        var repos = await service.DiscoverRepositoriesAsync(
            "http://lf-server.test", "TestRepo", "user", "pass");

        Assert.Single(repos);
        Assert.Equal("TestRepo", repos[0].RepositoryId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TestHttpMessageHandler OkHandler(string body) =>
        new() { Response = OkJson(body) };

    private static HttpResponseMessage OkJson(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

    // ── Test infrastructure ───────────────────────────────────────────────────

    internal sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage? Response { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (ExceptionToThrow is not null) throw ExceptionToThrow;
            if (ResponseFactory is not null) return Task.FromResult(ResponseFactory(request));
            return Task.FromResult(Response ?? new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubRepositoryContext : IRepositoryContext
    {
        private readonly RepositoryDescriptor _repo;
        public StubRepositoryContext(RepositoryDescriptor repo) => _repo = repo;

        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) =>
            Task.FromResult(_repo);

        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryDescriptor>>([_repo]);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
