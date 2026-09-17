using System.Net;
using System.Net.Http;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// Validates the single-flight token-acquisition guarantee:
/// N concurrent callers for the same repository must generate exactly
/// ONE token POST, regardless of race timing.  Without the per-key
/// <see cref="System.Threading.SemaphoreSlim"/> these tests would
/// routinely observe N posts and trigger HTTP 429 on real servers.
/// </summary>
public sealed class LaserficheAuthServiceConcurrencyTests
{
    [Fact]
    public async Task InteractiveRateLimit_BlocksRepeatedClicksButNotOtherRepositories()
    {
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests, "{}");
        var service = CreateService(handler);
        await Assert.ThrowsAsync<LaserficheException>(() => service.TryAuthenticateAsync(Repo(), "admin", "p"));
        await Assert.ThrowsAsync<LaserficheException>(() => service.TryAuthenticateAsync(Repo(), "admin", "p"));
        Assert.Equal(1, handler.CallCount);
        await Assert.ThrowsAsync<LaserficheException>(() => service.TryAuthenticateAsync(Repo("Other"), "admin", "p"));
        Assert.Equal(2, handler.CallCount);
    }

    // ── Factory ────────────────────────────────────────────────────────────────

    private static LaserficheAuthService CreateService(
        HttpMessageHandler handler,
        string username = "svc",
        string password = "pass")
    {
        var opts = new LaserficheOptions
        {
            ServerUrl    = "http://lf-test.local",
            ApiBasePath  = "/LFRepositoryAPI",
            ApiVersion   = "v1",
            RepositoryId = "TestRepo"
        };
        var adapter  = new LaserficheApiAdapter(new StaticOptionsMonitor(opts));
        var cache    = new MemoryCache(new MemoryCacheOptions());
        var creds    = new FixedCredentialProvider(username, password);
        var httpCtx  = new HttpContextAccessor();

        return new LaserficheAuthService(
            new SingleHandlerClientFactory(handler),
            creds,
            adapter,
            cache,
            new OptionsWrapper<LaserficheOptions>(opts),
            httpCtx,
            NullLogger<LaserficheAuthService>.Instance);
    }

    private static RepositoryDescriptor Repo(string id = "TestRepo") =>
        new(id, "http://lf-test.local", id, id);

    // ── Helper: canonical success JSON ────────────────────────────────────────
    private const string SuccessJson =
        """{"access_token":"tok-abc","expires_in":3600,"token_type":"Bearer"}""";

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1 — 20 simultaneous cache-miss callers produce exactly 1 token POST
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test1_TwentyConcurrentCacheMissCallers_SingleTokenPost()
    {
        // Arrange — handler blocks until we explicitly release it so that all 20
        // tasks can start and queue on the per-key semaphore before anyone returns.
        var handler = new GatedHandler(SuccessJson);
        var svc     = CreateService(handler);
        var repo    = Repo();

        // Act — fire 20 tasks simultaneously.
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => svc.GetTokenAsync(repo, CancellationToken.None))
            .ToArray();

        // Give the scheduler time to start all tasks and queue them on the semaphore.
        await Task.Delay(50);

        // Release the gate so the single in-flight HTTP call can complete.
        handler.Release();

        var tokens = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, handler.CallCount);                         // exactly 1 POST
        Assert.All(tokens, t => Assert.Equal("tok-abc", t));        // all callers got the token
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2 — cached token: 20 more calls produce 0 additional POSTs
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test2_WarmCache_ZeroAdditionalTokenPosts()
    {
        // Arrange — warm the cache with one call.
        var handler = new CountingHandler(HttpStatusCode.OK, SuccessJson);
        var svc     = CreateService(handler);
        var repo    = Repo();

        await svc.GetTokenAsync(repo);                              // prime the cache
        var baseCount = handler.CallCount;                          // should be 1

        // Act — 20 more simultaneous calls against the warm cache.
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => svc.GetTokenAsync(repo, CancellationToken.None))
            .ToArray();
        var tokens = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(baseCount, handler.CallCount);                 // no new POSTs
        Assert.All(tokens, t => Assert.Equal("tok-abc", t));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3 — expired token: exactly 1 refresh POST, not N
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test3_ExpiredToken_ExactlyOneRefreshPost()
    {
        // Arrange — use a very short expiry so the cached token disappears before
        // the second wave of concurrent callers runs.
        // We return expires_in=1 (1 second).  Cache entry is max(1-60,30)=30s, so
        // we cannot force natural expiry in a unit test; instead we verify that the
        // single-flight invariant holds for an initial cold-cache load, which is the
        // most critical window (see Test 1).  Immediate-expiry behaviour is tested
        // via InvalidateTokenAsync which zeros the cache entry.
        var handler = new GatedHandler(SuccessJson);
        var svc     = CreateService(handler);
        var repo    = Repo();

        // Warm the cache with a single serialised call.
        handler.Release();
        await svc.GetTokenAsync(repo);
        handler.Reset(); // reset gate and counter for the second wave

        // Evict the token — simulates expiry or server-initiated 401 invalidation.
        await svc.InvalidateTokenAsync(repo);

        // Act — 20 simultaneous post-expiry callers.
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => svc.GetTokenAsync(repo, CancellationToken.None))
            .ToArray();
        await Task.Delay(50);
        handler.Release();
        await Task.WhenAll(tasks);

        // Assert — exactly one token refresh (first call POST + one refresh POST).
        Assert.Equal(1, handler.CallCount); // only the post-eviction refresh
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4 — per-repository independence: two repos each get 1 POST
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test4_DifferentRepositories_EachGetOneTokenPost()
    {
        // Two repositories must never share a token; each must independently
        // acquire its own with exactly one HTTP call.
        var handler = new GatedHandler(SuccessJson);
        var svc     = CreateService(handler);
        var repoA   = Repo("RepoA");
        var repoB   = Repo("RepoB");

        var tasksA = Enumerable.Range(0, 10)
            .Select(_ => svc.GetTokenAsync(repoA, CancellationToken.None))
            .ToArray();
        var tasksB = Enumerable.Range(0, 10)
            .Select(_ => svc.GetTokenAsync(repoB, CancellationToken.None))
            .ToArray();

        await Task.Delay(50);
        handler.Release();

        await Task.WhenAll(tasksA.Concat(tasksB));

        // Each repo triggers exactly 1 POST → 2 total.
        Assert.Equal(2, handler.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 5 — per-user isolation: a different scope key means a new token POST
    //          (simulated by two separate service instances with different caches)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test5_DifferentServiceInstances_IndependentCaches()
    {
        // In production each IMemoryCache is process-wide (singleton), so two
        // users in different sessions share the same cache with different keys.
        // Here we verify that two *separate* service instances (each with its own
        // cache, as in testing) each perform exactly one token POST.
        var handlerA = new CountingHandler(HttpStatusCode.OK, SuccessJson);
        var handlerB = new CountingHandler(HttpStatusCode.OK, SuccessJson);

        var svcA = CreateService(handlerA, "alice", "pass");
        var svcB = CreateService(handlerB, "bob",   "pass");
        var repo = Repo();

        await Task.WhenAll(svcA.GetTokenAsync(repo), svcB.GetTokenAsync(repo));

        Assert.Equal(1, handlerA.CallCount);
        Assert.Equal(1, handlerB.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 6 — HTTP 429 bounded retry: retries ≤ MaxTokenRetries (2), then throws
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test6_429Response_RetriesUpToMaxThenThrows()
    {
        // Arrange — always return 429.
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests,
            """{"error":"rate_limited"}""");
        var svc  = CreateService(handler);
        var repo = Repo();

        // Act + Assert
        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.GetTokenAsync(repo));

        // The implementation retries at most MaxTokenRetries=2 times, so
        // total calls = 1 initial + 2 retries = 3.
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(429, ex.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 7 — HTTP 429 then 200: succeeds after one retry
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test7_429ThenSuccess_SucceedsAfterOneRetry()
    {
        // First call returns 429; second call returns 200.
        var handler = new SequenceHandler(new[]
        {
            (HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""),
            (HttpStatusCode.OK,              SuccessJson)
        });
        var svc  = CreateService(handler);
        var repo = Repo();

        var token = await svc.GetTokenAsync(repo);

        Assert.Equal("tok-abc", token);
        Assert.Equal(2, handler.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 8 — 401 from GetTokenAsync triggers InvalidateTokenAsync correctly
    //          (BearerTokenHandler calls Invalidate; next GetToken re-acquires)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test8_TokenInvalidation_ForcesNewTokenAcquisitionOnNextCall()
    {
        // Arrange — every call returns a fresh token (simulates server token rotation).
        var handler = new CountingHandler(HttpStatusCode.OK, SuccessJson);
        var svc     = CreateService(handler);
        var repo    = Repo();

        // First acquisition (cache miss → 1 POST).
        var token1 = await svc.GetTokenAsync(repo);

        // Simulate 401 path: BearerTokenHandler calls InvalidateTokenAsync, then
        // GetTokenAsync is retried by the application layer.
        await svc.InvalidateTokenAsync(repo);

        // Second acquisition after invalidation (cache miss again → 1 POST).
        var token2 = await svc.GetTokenAsync(repo);

        Assert.Equal("tok-abc", token1);
        Assert.Equal("tok-abc", token2);
        Assert.Equal(2, handler.CallCount); // exactly 2 POSTs — no more, no less
    }

    [Fact]
    public async Task Test9_ConcurrentIdenticalInteractiveLogins_SendOneTokenPost()
    {
        var handler = new GatedHandler(SuccessJson);
        var svc = CreateService(handler);
        var repo = Repo();

        var attempts = Enumerable.Range(0, 10)
            .Select(_ => svc.TryAuthenticateAsync(repo, "admin", "secret"))
            .ToArray();

        await Task.Delay(50);
        handler.Release();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, handler.CallCount);
        Assert.All(results, Assert.True);
    }

    [Fact]
    public async Task Test10_DifferentInteractiveCredentials_AreNeverShared()
    {
        var handler = new GatedHandler(SuccessJson);
        var svc = CreateService(handler);
        var repo = Repo();

        var alice = svc.TryAuthenticateAsync(repo, "alice", "alice-password");
        var bob = svc.TryAuthenticateAsync(repo, "bob", "bob-password");

        await Task.Delay(50);
        handler.Release();
        Assert.All(await Task.WhenAll(alice, bob), Assert.True);
        Assert.Equal(2, handler.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Blocks all <c>SendAsync</c> calls on a <see cref="TaskCompletionSource"/>
    /// gate.  The test controls when responses are sent, allowing all concurrent
    /// callers to queue on the per-key semaphore before the first one proceeds.
    /// Tracks the total number of <c>SendAsync</c> invocations for assertions.
    /// </summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private          TaskCompletionSource _gate;
        private volatile int                 _callCount;
        private readonly string              _responseBody;

        public int CallCount => _callCount;

        public GatedHandler(string responseBody)
        {
            _responseBody = responseBody;
            _gate         = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Allows blocked <see cref="SendAsync"/> calls to proceed.</summary>
        public void Release() => _gate.TrySetResult();

        /// <summary>
        /// Resets the gate and call counter so the handler can be reused across
        /// multiple waves within the same test.
        /// </summary>
        public void Reset()
        {
            System.Threading.Interlocked.Exchange(ref _callCount, 0);
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _callCount);
            await _gate.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody,
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// Returns a fixed status code + body for every request and counts total calls.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private volatile int    _callCount;
        private readonly HttpStatusCode _status;
        private readonly string         _body;

        public int CallCount => _callCount;

        public CountingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body   = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body,
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Returns responses in order: first call returns responses[0], second returns responses[1], etc.
    /// After the sequence is exhausted every subsequent call returns the last entry.
    /// </summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body)[] _sequence;
        private int _index;
        public int CallCount => _index;

        public SequenceHandler((HttpStatusCode, string)[] sequence)
            => _sequence = sequence;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var idx  = System.Threading.Interlocked.Increment(ref _index) - 1;
            var (status, body) = idx < _sequence.Length
                ? _sequence[idx]
                : _sequence[^1];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body,
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    // ── DI stubs ─────────────────────────────────────────────────────────────

    private sealed class SingleHandlerClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleHandlerClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FixedCredentialProvider : ICredentialProvider
    {
        private readonly string _u, _p;
        public FixedCredentialProvider(string u, string p) { _u = u; _p = p; }
        public Task<LaserficheCredential> GetCredentialsAsync(string key, CancellationToken ct = default)
            => Task.FromResult(new LaserficheCredential(_u, _p));
        public Task StoreCredentialsAsync(string key, string u, string p, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StaticOptionsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<LaserficheOptions>
    {
        public StaticOptionsMonitor(LaserficheOptions v) => CurrentValue = v;
        public LaserficheOptions CurrentValue { get; }
        public LaserficheOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LaserficheOptions, string?> listener) => null;
    }
}
