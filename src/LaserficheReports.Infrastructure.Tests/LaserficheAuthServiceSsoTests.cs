using System.Net;
using System.Security.Claims;
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
/// Unit tests for <see cref="LaserficheAuthService.ExchangeAuthorizationCodeAsync"/>:
/// URL correctness, form-field contract, error-status handling, token caching,
/// and the guarantee that authorization codes and code verifiers are never logged.
/// </summary>
public sealed class LaserficheAuthServiceSsoTests
{
    // ── Factories ─────────────────────────────────────────────────────────────

    private static LaserficheAuthService CreateService(
        LaserficheAuthServiceTokenTests.TestHttpMessageHandler handler,
        LaserficheOptions? options = null,
        LaserficheAuthServiceTokenTests.TestLogger? logger = null)
    {
        var opts = options ?? new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v1",
        };

        var adapter = new LaserficheApiAdapter(new StaticOptionsMonitor<LaserficheOptions>(opts));
        var cache   = new MemoryCache(new MemoryCacheOptions());
        var creds   = new FixedCredentialProvider("testuser", "testpass");
        var log     = logger ?? new LaserficheAuthServiceTokenTests.TestLogger();
        var httpCtx = new HttpContextAccessor();

        return new LaserficheAuthService(
            new TestHttpClientFactory(handler),
            creds,
            adapter,
            cache,
            new OptionsWrapper<LaserficheOptions>(opts),
            httpCtx,
            NullLogger<LaserficheAuthService>.Instance.WrapWithTest(log));
    }

    private static RepositoryDescriptor MakeRepo(string id = "TestRepo") =>
        new(id, "http://lf-server.test", id, id);

    // ─────────────────────────────────────────────────────────────────────────
    // Token URL — always V2 regardless of EffectiveApiVersion
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCode_V1Config_SendsToV2TokenUrl()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v1",   // resource operations would use v1
        });

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo("Docs"), "auth-code", "verifier", "https://host/Login/Callback", "LFDashboard");

        // Token exchange MUST use v2, even though resource API uses v1.
        Assert.Equal(
            "http://lf-server.test/LFRepositoryAPI/v2/Repositories/Docs/Token",
            handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ExchangeCode_V2Config_SendsToV2TokenUrl()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v2",
        });

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo("Archive"), "auth-code", "verifier", "https://host/Login/Callback", "LFDashboard");

        Assert.Equal(
            "http://lf-server.test/LFRepositoryAPI/v2/Repositories/Archive/Token",
            handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ExchangeCode_RepositoryNameWithSpaces_IsUrlEncoded()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v1",
        });

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo("My Repository"), "code", "verifier", "https://host/Login/Callback", "LFDashboard");

        var uri = handler.LastRequestUri?.ToString();
        Assert.NotNull(uri);
        Assert.Contains("/Repositories/My%20Repository/Token", uri);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Request form fields
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCode_SendsCorrectGrantType()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler);

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "the-code", "the-verifier", "https://host/Login/Callback", "LFDashboard");

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("grant_type=authorization_code", handler.LastRequestBody!);
    }

    [Fact]
    public async Task ExchangeCode_SendsAllRequiredFormFields()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler);

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "my-code", "my-verifier",
            "https://host/Login/Callback", "MyClientId");

        var body = handler.LastRequestBody!;
        Assert.NotNull(body);
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=",          body);
        Assert.Contains("code_verifier=", body);
        Assert.Contains("redirect_uri=",  body);
        Assert.Contains("client_id=",     body);
    }

    [Fact]
    public async Task ExchangeCode_SendsFormUrlEncoded()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler);

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard");

        Assert.Equal(
            "application/x-www-form-urlencoded",
            handler.LastRequest?.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ExchangeCode_ClientIdIsTransmitted()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler);

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "code", "verifier", "https://host/Login/Callback", "SpecialClientApp");

        Assert.Contains("client_id=SpecialClientApp", handler.LastRequestBody!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTTP status handling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCode_200_ReturnsTrue()
    {
        var svc = CreateService(SuccessHandler());
        var result = await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard");
        Assert.True(result);
    }

    [Fact]
    public async Task ExchangeCode_400_ThrowsDiagnosticException()
    {
        var svc = CreateService(StatusHandler(HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<LaserficheException>(() => svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task ExchangeCode_401_ThrowsDiagnosticException()
    {
        // 401 = code already used or expired
        var svc = CreateService(StatusHandler(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<LaserficheException>(() => svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard"));
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task ExchangeCode_403_UntrustedSaml_PreservesSanitizedDiagnostic()
    {
        var svc = CreateService(StatusHandler(
            HttpStatusCode.Forbidden,
            "Received an invalid or untrusted SAML token. [9530]"));
        var ex = await Assert.ThrowsAsync<LaserficheException>(() => svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard"));
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("invalid or untrusted SAML token", ex.ResponseBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeCode_500_Throws_LaserficheException()
    {
        var svc = CreateService(StatusHandler(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.ExchangeAuthorizationCodeAsync(
                MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard"));

        Assert.Equal(500, ex.StatusCode);
        Assert.NotNull(ex.DiagnosticId);
    }

    [Fact]
    public async Task ExchangeCode_NetworkFailure_PropagatesException()
    {
        var handler = new LaserficheAuthServiceTokenTests.TestHttpMessageHandler
        {
            ExceptionToThrow = new HttpRequestException("Connection refused")
        };
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.ExchangeAuthorizationCodeAsync(
                MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Token caching — SSO token must be found by GetTokenAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCode_Success_TokenIsFoundByGetTokenAsync()
    {
        // After a successful exchange, GetTokenAsync must return the cached SSO token
        // without triggering another HTTP request (BearerTokenHandler compatibility).
        var exchangeHandler  = SuccessHandler("sso-bearer-token-xyz");

        // GetTokenAsync would use a different handler if it hit the network.
        // We verify no second request is issued by checking the call count.
        int callCount = 0;
        var countingHandler  = new CountingHttpMessageHandler(
            new LaserficheAuthServiceTokenTests.TestHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        @"{""access_token"":""fallback-token"",""expires_in"":3600}",
                        System.Text.Encoding.UTF8, "application/json")
                }
            },
            () => callCount++);

        var opts = new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v1",
        };
        var adapter = new LaserficheApiAdapter(new StaticOptionsMonitor<LaserficheOptions>(opts));
        var cache   = new MemoryCache(new MemoryCacheOptions());
        var httpCtx = new HttpContextAccessor();

        // Factory returns the exchange handler for the FIRST request, counting handler thereafter.
        var factory = new SequentialHttpClientFactory([exchangeHandler, countingHandler]);

        var svc = new LaserficheAuthService(
            factory,
            new FixedCredentialProvider("u", "p"),
            adapter,
            cache,
            new OptionsWrapper<LaserficheOptions>(opts),
            httpCtx,
            NullLogger<LaserficheAuthService>.Instance);

        var repo = MakeRepo("Docs");

        // Exchange stores token in cache.
        await svc.ExchangeAuthorizationCodeAsync(repo, "code", "verifier",
            "https://host/Login/Callback", "LFDashboard");

        // GetTokenAsync should find the cached token — no second HTTP call.
        var token = await svc.GetTokenAsync(repo);

        Assert.Equal("sso-bearer-token-xyz", token);
        Assert.Equal(0, callCount); // counting handler never hit
    }

    [Theory]
    [InlineData("LFDS")]
    [InlineData("RepositoryPassword")]
    public async Task GetToken_InteractivePrincipalWithCacheMiss_NeverUsesFallbackCredentials(
        string authenticationMethod)
    {
        var opts = new LaserficheOptions
        {
            ServerUrl = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
        };
        var adapter = new LaserficheApiAdapter(new StaticOptionsMonitor<LaserficheOptions>(opts));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.AuthenticationMethod, authenticationMethod)],
                "Dashboard.Cookie")),
        };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var credentials = new ThrowingCredentialProvider();

        var svc = new LaserficheAuthService(
            new TestHttpClientFactory(SuccessHandler("must-not-be-requested")),
            credentials,
            adapter,
            new MemoryCache(new MemoryCacheOptions()),
            new OptionsWrapper<LaserficheOptions>(opts),
            accessor,
            NullLogger<LaserficheAuthService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetTokenAsync(MakeRepo()));
        Assert.Equal(0, credentials.CallCount);
    }

    [Fact]
    public async Task TokenCache_IsNotSharedBetweenUsersInSameRepository()
    {
        var opts = new LaserficheOptions { ServerUrl = "http://lf-server.test", ApiBasePath = "/LFRepositoryAPI" };
        var adapter = new LaserficheApiAdapter(new StaticOptionsMonitor<LaserficheOptions>(opts));
        var accessor = new HttpContextAccessor();
        var factory = new SequentialHttpClientFactory([
            SuccessHandler("amjd-token"),
            SuccessHandler("admin-token")
        ]);
        var service = new LaserficheAuthService(
            factory,
            new ThrowingCredentialProvider(),
            adapter,
            new MemoryCache(new MemoryCacheOptions()),
            new OptionsWrapper<LaserficheOptions>(opts),
            accessor,
            NullLogger<LaserficheAuthService>.Instance);
        var repo = MakeRepo("TestEmployee");

        accessor.HttpContext = UserContext("same-browser-session", "amjd");
        Assert.True(await service.TryAuthenticateAsync(repo, "amjd", "pw"));
        Assert.Equal("amjd-token", await service.GetTokenAsync(repo));

        await service.InvalidateCurrentSessionTokensAsync();
        accessor.HttpContext = UserContext("same-browser-session", "admin");
        Assert.True(await service.TryAuthenticateAsync(repo, "admin", "pw"));
        Assert.Equal("admin-token", await service.GetTokenAsync(repo));
    }

    private static DefaultHttpContext UserContext(string sessionId, string username)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.AuthenticationMethod, "RepositoryPassword")
            ], "Dashboard.Cookie"))
        };
        var session = new TestSession(sessionId);
        session.Set("AuthenticatedRepositoryId", [1]);
        session.SetString("AuthenticationScopeMethod", "RepositoryPassword");
        session.SetString("AuthenticationScopeSubject", username);
        context.Session = session;
        return context;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Secrets must not appear in logs
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCode_AuthCode_NeverInLogs()
    {
        const string secretCode = "super-secret-auth-code-ABCDEF";
        var logger  = new LaserficheAuthServiceTokenTests.TestLogger();
        var svc     = CreateService(SuccessHandler(), logger: logger);

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), secretCode, "verifier", "https://host/Login/Callback", "LFDashboard");

        foreach (var entry in logger.Entries)
            Assert.DoesNotContain(secretCode, entry);
    }

    [Fact]
    public async Task ExchangeCode_CodeVerifier_NeverInLogs()
    {
        const string secretVerifier = "super-secret-verifier-XYZ789";
        var logger = new LaserficheAuthServiceTokenTests.TestLogger();
        var svc    = CreateService(SuccessHandler(), logger: logger);

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "some-code", secretVerifier,
            "https://host/Login/Callback", "LFDashboard");

        foreach (var entry in logger.Entries)
            Assert.DoesNotContain(secretVerifier, entry);
    }

    [Fact]
    public async Task ExchangeCode_TokenInSuccessResponse_IsRedactedInLogs()
    {
        const string tokenValue = "actual-bearer-token-value-123";
        var logger  = new LaserficheAuthServiceTokenTests.TestLogger();
        var svc     = CreateService(SuccessHandler(tokenValue), logger: logger);

        await svc.ExchangeAuthorizationCodeAsync(
            MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard");

        // The token value must never appear in any log message.
        foreach (var entry in logger.Entries)
            Assert.DoesNotContain(tokenValue, entry);
    }

    [Fact]
    public async Task ExchangeCode_500_AccessTokenInErrorBody_IsRedacted()
    {
        const string body = @"{""access_token"":""leaked-token"",""error"":""server_error""}";
        var logger = new LaserficheAuthServiceTokenTests.TestLogger();
        var svc    = CreateService(
            StatusHandler(HttpStatusCode.InternalServerError, body),
            logger: logger);

        try
        {
            await svc.ExchangeAuthorizationCodeAsync(
                MakeRepo(), "code", "verifier", "https://host/Login/Callback", "LFDashboard");
        }
        catch (LaserficheException) { /* expected */ }

        foreach (var entry in logger.Entries)
            Assert.DoesNotContain("leaked-token", entry);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Response factories
    // ─────────────────────────────────────────────────────────────────────────

    private static LaserficheAuthServiceTokenTests.TestHttpMessageHandler SuccessHandler(
        string accessToken = "test-sso-token")
    {
        var json = $@"{{""access_token"":""{accessToken}"",""expires_in"":3600,""token_type"":""Bearer""}}";
        return new LaserficheAuthServiceTokenTests.TestHttpMessageHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }
        };
    }

    private static LaserficheAuthServiceTokenTests.TestHttpMessageHandler StatusHandler(
        HttpStatusCode status, string? body = null)
    {
        var content = body ?? @"{""error"":""invalid_grant""}";
        return new LaserficheAuthServiceTokenTests.TestHttpMessageHandler
        {
            Response = new HttpResponseMessage(status)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test infrastructure
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CountingHttpMessageHandler(
        HttpMessageHandler inner,
        Action             onSend)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            onSend();
            // Delegate to inner for the actual response.
            using var innerClient = new HttpClient(inner, disposeHandler: false);
            return await innerClient.SendAsync(request, ct);
        }
    }

    /// <summary>
    /// Returns handlers in sequence; falls back to the last one when exhausted.
    /// </summary>
    private sealed class SequentialHttpClientFactory : IHttpClientFactory
    {
        private readonly List<HttpMessageHandler> _handlers;
        private int _index;

        public SequentialHttpClientFactory(IReadOnlyList<HttpMessageHandler> handlers)
        {
            _handlers = [..handlers];
        }

        public HttpClient CreateClient(string name)
        {
            var handler = _handlers[Math.Min(_index, _handlers.Count - 1)];
            _index++;
            return new HttpClient(handler, disposeHandler: false);
        }
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

    private sealed class ThrowingCredentialProvider : ICredentialProvider
    {
        public int CallCount { get; private set; }

        public Task<LaserficheCredential> GetCredentialsAsync(
            string key,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("Fallback credentials must not be read.");
        }

        public Task StoreCredentialsAsync(
            string key,
            string username,
            string password,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestSession(string id) : ISession
    {
        private readonly Dictionary<string, byte[]> _values = [];
        public bool IsAvailable => true;
        public string Id => id;
        public IEnumerable<string> Keys => _values.Keys;
        public void Clear() => _values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }
}

// ── Extension helpers ─────────────────────────────────────────────────────────

file static class SsoTestLoggerExtensions
{
    public static Microsoft.Extensions.Logging.ILogger<T> WrapWithTest<T>(
        this Microsoft.Extensions.Logging.ILogger<T> inner,
        LaserficheAuthServiceTokenTests.TestLogger    sink)
        => new ForwardingLogger<T>(inner, sink);

    private sealed class ForwardingLogger<T>(
        Microsoft.Extensions.Logging.ILogger<T>   inner,
        LaserficheAuthServiceTokenTests.TestLogger sink)
        : Microsoft.Extensions.Logging.ILogger<T>
    {
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel       logLevel,
            Microsoft.Extensions.Logging.EventId        eventId,
            TState                                       state,
            Exception?                                   exception,
            Func<TState, Exception?, string>             formatter)
        {
            sink.Log(formatter(state, exception));
            inner.Log(logLevel, eventId, state, exception, formatter);
        }

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) =>
            inner.IsEnabled(level) || level >= Microsoft.Extensions.Logging.LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(state);
    }
}

file sealed class StaticOptionsMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
