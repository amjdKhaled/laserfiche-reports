using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Unit tests for <see cref="LaserficheAuthService"/> token-acquisition behaviour:
/// URL correctness, form-field contract, error-status handling, diagnostic ID
/// generation, and the guarantee that passwords never appear in logged output.
/// </summary>
public sealed class LaserficheAuthServiceTokenTests
{
    // ── Factories ─────────────────────────────────────────────────────────────

    private static LaserficheAuthService CreateService(
        TestHttpMessageHandler handler,
        LaserficheOptions?     options     = null,
        ICredentialProvider?   credentials = null,
        TestLogger?            logger      = null)
    {
        var opts = options ?? new LaserficheOptions
        {
            ServerUrl    = "http://lf-server.test",
            ApiBasePath  = "/LFRepositoryAPI",
            ApiVersion   = "v1",
            RepositoryId = "TestRepo"
        };

        var adapter   = new LaserficheApiAdapter(new StaticOptionsMonitor<LaserficheOptions>(opts));
        var cache     = new MemoryCache(new MemoryCacheOptions());
        var creds     = credentials ?? new FixedCredentialProvider("testuser", "testpass");
        var log       = logger ?? new TestLogger();
        var httpCtx   = new HttpContextAccessor(); // no active context in tests

        return new LaserficheAuthService(
            new TestHttpClientFactory(handler),
            creds,
            adapter,
            cache,
            new Microsoft.Extensions.Options.OptionsWrapper<LaserficheOptions>(opts),
            httpCtx,
            NullLogger<LaserficheAuthService>.Instance.WrapWithTest(log));
    }

    // RepositoryDescriptor(Key, ServerUrl, RepositoryId, DisplayName)
    private static RepositoryDescriptor MakeRepo(string id = "TestRepo") =>
        new(id, "http://lf-server.test", id, id);

    // ─────────────────────────────────────────────────────────────────────────
    // Token URL format
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAuthenticate_V1ResourceConfig_StillUsesV2InteractiveTokenUrl()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v1"
        });

        await svc.TryAuthenticateAsync(MakeRepo("Docs"), "u", "p");

        Assert.Equal(
            "http://lf-server.test/LFRepositoryAPI/v2/Repositories/Docs/Token",
            handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task TryAuthenticate_V2_SendsToCorrectTokenUrl()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v2"
        });

        await svc.TryAuthenticateAsync(MakeRepo("Archive"), "u", "p");

        Assert.Equal(
            "http://lf-server.test/LFRepositoryAPI/v2/Repositories/Archive/Token",
            handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task TryAuthenticate_RepositoryNameWithSpaces_IsUrlEncoded()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v1"
        });

        await svc.TryAuthenticateAsync(MakeRepo("My Repository"), "u", "p");

        // Space must be percent-encoded to %20 in the path segment.
        var uri = handler.LastRequestUri?.ToString();
        Assert.NotNull(uri);
        Assert.Contains("/Repositories/My%20Repository/Token", uri);
        Assert.DoesNotContain("/Repositories/My Repository/Token", uri);
    }

    [Fact]
    public async Task TryAuthenticate_RepositoryNameWithAmpersand_IsUrlEncoded()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, new LaserficheOptions
        {
            ServerUrl   = "http://lf-server.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion  = "v1"
        });

        await svc.TryAuthenticateAsync(MakeRepo("A&B"), "u", "p");

        var uri = handler.LastRequestUri?.ToString();
        Assert.NotNull(uri);
        Assert.Contains("/Repositories/A%26B/Token", uri);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Token request format (Content-Type, form fields)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAuthenticate_Request_UsesFormUrlEncoded()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler);

        await svc.TryAuthenticateAsync(MakeRepo(), "user1", "pass1");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "application/x-www-form-urlencoded",
            handler.LastRequest!.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TryAuthenticate_Request_SendsGrantTypePasswordUsernamePassword()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler);

        await svc.TryAuthenticateAsync(MakeRepo(), "myuser", "mypass");

        Assert.NotNull(handler.LastRequestBody);
        var body = handler.LastRequestBody!;

        // Verify field names (not values) are present.
        Assert.Contains("grant_type=password", body);
        Assert.Contains("username=", body);
        Assert.Contains("password=", body);
    }

    [Fact]
    public async Task TryAuthenticate_EmptyPassword_SendsEmptyPasswordField()
    {
        var handler = SuccessHandler();
        var svc = CreateService(handler, credentials: new FixedCredentialProvider("u", ""));

        await svc.TryAuthenticateAsync(MakeRepo(), "u", "");

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("password=", handler.LastRequestBody!);
        // Value after password= should be either nothing or the next & separator.
        Assert.Matches(@"password=($|&)", handler.LastRequestBody!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTTP status-code handling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAuthenticate_200_ReturnsTrue()
    {
        var svc = CreateService(SuccessHandler());
        Assert.True(await svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));
    }

    [Fact]
    public async Task TryAuthenticate_401_ReturnsFalse_DoesNotThrow()
    {
        var svc = CreateService(StatusHandler(HttpStatusCode.Unauthorized));
        Assert.False(await svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));
    }

    [Fact]
    public async Task TryAuthenticate_403_ReturnsFalse_DoesNotThrow()
    {
        var svc = CreateService(StatusHandler(HttpStatusCode.Forbidden));
        Assert.False(await svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));
    }

    [Fact]
    public async Task TryAuthenticate_400_ReturnsFalse_DoesNotThrow()
    {
        // 400 is treated as a credential error (invalid request), same as 401.
        var svc = CreateService(StatusHandler(HttpStatusCode.BadRequest));
        Assert.False(await svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));
    }

    [Fact]
    public async Task TryAuthenticate_404_Throws_LaserficheException_StatusCode404()
    {
        // 404 means the repository does not exist; should propagate so the caller
        // can show a "repository not found" message.
        var svc = CreateService(StatusHandler(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task TryAuthenticate_429_ThrowsAfterOneRequest_WithoutAutomaticRetries()
    {
        var handler = StatusHandler(HttpStatusCode.TooManyRequests);
        var svc = CreateService(handler);

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TryAuthenticate_500_Throws_LaserficheException_StatusCode500()
    {
        var svc = CreateService(StatusHandler(HttpStatusCode.InternalServerError,
            @"{""title"":""Internal Server Error"",""status"":500}"));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task TryAuthenticate_500_Exception_HasDiagnosticId()
    {
        var svc = CreateService(StatusHandler(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        // DiagnosticId must be set and be exactly 8 uppercase hex characters.
        Assert.NotNull(ex.DiagnosticId);
        Assert.Matches("^[0-9A-F]{8}$", ex.DiagnosticId!);
    }

    [Fact]
    public async Task TryAuthenticate_500_Exception_HasSanitizedResponseBody()
    {
        const string responseJson = @"{""title"":""Internal Server Error"",""status"":500,""detail"":""Something failed""}";
        var svc = CreateService(StatusHandler(HttpStatusCode.InternalServerError, responseJson));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        Assert.NotNull(ex.ResponseBody);
        Assert.Contains("Internal Server Error", ex.ResponseBody!);
        Assert.Contains("Something failed", ex.ResponseBody!);
    }

    [Fact]
    public async Task TryAuthenticate_500_WithLFErrorCode_ParsedIntoException()
    {
        const string body = @"{""errorCode"":9001,""message"":""Repository not accessible""}";
        var svc = CreateService(StatusHandler(HttpStatusCode.InternalServerError, body));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        Assert.Equal("9001", ex.LFErrorCode);
    }

    [Fact]
    public async Task TryAuthenticate_500_AccessTokenInErrorBody_IsRedacted()
    {
        // Even though this is an unusual error shape, any access_token in the body
        // must be redacted before it appears in the exception or logs.
        const string body = @"{""access_token"":""secret-token"",""error"":""server_error""}";
        var svc = CreateService(StatusHandler(HttpStatusCode.InternalServerError, body));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        Assert.NotNull(ex.ResponseBody);
        Assert.DoesNotContain("secret-token", ex.ResponseBody!);
        Assert.Contains("[REDACTED]", ex.ResponseBody!);
    }

    [Fact]
    public async Task TryAuthenticate_MalformedJsonResponse_ErrorBody_IsPreserved()
    {
        // Non-JSON 500 body (plain text) must not crash the service.
        const string plainTextError = "Internal Server Error: configuration failure";
        var svc = CreateService(StatusHandler(HttpStatusCode.InternalServerError, plainTextError));

        var ex = await Assert.ThrowsAsync<LaserficheException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));

        Assert.Equal(500, ex.StatusCode);
        Assert.NotNull(ex.DiagnosticId);
        // Body preserved as-is when not valid JSON.
        Assert.Contains(plainTextError, ex.ResponseBody!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Transport failures
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAuthenticate_NetworkFailure_PropagatesHttpRequestException()
    {
        var handler = new TestHttpMessageHandler
        {
            ExceptionToThrow = new HttpRequestException(
                "Connection refused", null, HttpStatusCode.ServiceUnavailable)
        };
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));
    }

    [Fact]
    public async Task TryAuthenticate_TlsFailure_PropagatesHttpRequestException()
    {
        var handler = new TestHttpMessageHandler
        {
            ExceptionToThrow = new HttpRequestException(
                "SSL/TLS error", new Exception("certificate chain error"))
        };
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.TryAuthenticateAsync(MakeRepo(), "u", "p"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Password never appears in logs
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAuthenticate_Success_PasswordNeverInLogs()
    {
        const string secretPassword = "S3cr3tP@ssword!";
        var logger  = new TestLogger();
        var handler = SuccessHandler();
        var svc     = CreateService(handler,
            credentials: new FixedCredentialProvider("user", secretPassword),
            logger: logger);

        await svc.TryAuthenticateAsync(MakeRepo(), "user", secretPassword);

        foreach (var entry in logger.Entries)
            Assert.DoesNotContain(secretPassword, entry);
    }

    [Fact]
    public async Task TryAuthenticate_Failure500_PasswordNeverInLogs()
    {
        const string secretPassword = "T0pS3cr3t!";
        const string responseBody   = @"{""error"":""server_error"",""password"":""T0pS3cr3t!""}";
        var logger  = new TestLogger();
        var handler = StatusHandler(HttpStatusCode.InternalServerError, responseBody);
        var svc     = CreateService(handler,
            credentials: new FixedCredentialProvider("user", secretPassword),
            logger: logger);

        try { await svc.TryAuthenticateAsync(MakeRepo(), "user", secretPassword); }
        catch (LaserficheException) { /* expected */ }

        foreach (var entry in logger.Entries)
            Assert.DoesNotContain(secretPassword, entry);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers — response factories
    // ─────────────────────────────────────────────────────────────────────────

    private static TestHttpMessageHandler SuccessHandler()
    {
        const string json = @"{""access_token"":""test-token-value"",""expires_in"":3600,""token_type"":""Bearer""}";
        return new TestHttpMessageHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }
        };
    }

    private static TestHttpMessageHandler StatusHandler(
        HttpStatusCode status,
        string?        body = null)
    {
        var content = body ?? @"{""error"":""request_failed""}";
        return new TestHttpMessageHandler
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

    /// <summary>Captures the last HTTP request sent and optionally throws an exception.</summary>
    internal sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage? Response         { get; set; }
        public Exception?           ExceptionToThrow { get; set; }
        public HttpRequestMessage?  LastRequest      { get; private set; }
        /// <summary>
        /// Returns <see cref="Uri.AbsoluteUri"/> (always percent-encoded) rather than
        /// <see cref="Uri.ToString()"/>, which unescapes %20 and similar sequences.
        /// </summary>
        public string? LastRequestUri  => LastRequest?.RequestUri?.AbsoluteUri;
        public string? LastRequestBody { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);

            if (ExceptionToThrow is not null) throw ExceptionToThrow;

            return Response ?? new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// <see cref="IHttpClientFactory"/> that always returns a client backed by
    /// the provided <see cref="HttpMessageHandler"/> — without disposing it.
    /// </summary>
    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>Fixed credentials for testing — never reads disk or session.</summary>
    private sealed class FixedCredentialProvider : ICredentialProvider
    {
        private readonly string _username;
        private readonly string _password;
        public FixedCredentialProvider(string username, string password)
        {
            _username = username;
            _password = password;
        }
        public Task<LaserficheCredential> GetCredentialsAsync(
            string repositoryKey, CancellationToken ct = default)
            => Task.FromResult(new LaserficheCredential(_username, _password));
        public Task StoreCredentialsAsync(
            string repositoryKey, string username, string password,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>Captures formatted log messages for post-test assertions.</summary>
    internal sealed class TestLogger
    {
        private readonly List<string> _entries = [];
        public IReadOnlyList<string> Entries => _entries;
        public void Log(string message) => _entries.Add(message);
    }
}

// ── Extension helpers ────────────────────────────────────────────────────────

/// <summary>
/// Wraps a <see cref="Microsoft.Extensions.Logging.ILogger{T}"/> so every formatted
/// log message is also forwarded to a <see cref="LaserficheAuthServiceTokenTests.TestLogger"/>.
/// </summary>
file static class LoggerTestExtensions
{
    public static Microsoft.Extensions.Logging.ILogger<T> WrapWithTest<T>(
        this Microsoft.Extensions.Logging.ILogger<T> inner,
        LaserficheAuthServiceTokenTests.TestLogger    sink)
        => new ForwardingLogger<T>(inner, sink);

    private sealed class ForwardingLogger<T>(
        Microsoft.Extensions.Logging.ILogger<T>     inner,
        LaserficheAuthServiceTokenTests.TestLogger   sink)
        : Microsoft.Extensions.Logging.ILogger<T>
    {
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel                       logLevel,
            Microsoft.Extensions.Logging.EventId                        eventId,
            TState                                                       state,
            Exception?                                                   exception,
            Func<TState, Exception?, string>                             formatter)
        {
            var message = formatter(state, exception);
            sink.Log(message);
            inner.Log(logLevel, eventId, state, exception, formatter);
        }

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
            => inner.IsEnabled(logLevel) || logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => inner.BeginScope(state);
    }
}

/// <summary>Minimal fixed-value <see cref="IOptionsMonitor{T}"/> for tests.</summary>
file sealed class StaticOptionsMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
