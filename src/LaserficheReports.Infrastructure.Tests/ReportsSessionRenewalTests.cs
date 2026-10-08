using System.Net;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class ReportsSessionRenewalTests
{
    private static readonly RepositoryDescriptor Repo = new("TestRepo", "http://lf.test", "TestRepo", "TestRepo");
    private static (LaserficheAuthService, Handler) Create(params HttpResponseMessage[] responses)
    {
        var session = new Session();
        session.SetString("AuthenticationScopeMethod", "Reports");
        session.SetString("ActiveRepositoryId", Repo.RepositoryId);
        var options = new LaserficheOptions { ServerUrl = Repo.ServerUrl, ApiBasePath = "/LFRepositoryAPI", ApiVersion = "v2", RepositoryId = Repo.RepositoryId };
        var handler = new Handler(responses);
        var service = new LaserficheAuthService(handler, new ForbiddenDisk(),
            new LaserficheApiAdapter(new Monitor(options)),
            new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Options.Options.Create(options),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { Session = session } },
            NullLogger<LaserficheAuthService>.Instance, new Credentials());
        return (service, handler);
    }
    private static HttpResponseMessage Response(HttpStatusCode status, string body = "{}") => new(status) { Content = new StringContent(body) };
    private static HttpResponseMessage Token(string name, bool refresh = false) => Response(HttpStatusCode.OK,
        "{\"access_token\":\"" + name + "\",\"expires_in\":900" + (refresh ? ",\"refresh_token\":\"renewal\"" : "") + "}");

    [Fact]
    public async Task RejectedRefresh_RecoversSameUserOnceForConcurrentReads()
    {
        var (service, handler) = Create(Token("old", true), Response(HttpStatusCode.Unauthorized), Token("new"));
        await service.TryAuthenticateAsync(Repo, "same-user", "same-password");
        await service.InvalidateTokenAsync(Repo);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GetTokenAsync(Repo)));
        Assert.All(tokens, token => Assert.Equal("new", token));
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("grant_type=refresh_token", handler.Bodies[1]);
        Assert.Contains("username=same-user", handler.Bodies[2]);
        Assert.Contains("password=same-password", handler.Bodies[2]);
    }
    [Fact]
    public async Task NoRefreshToken_RenewsFromSelectedSessionWithoutDiskFallback()
    {
        var (service, handler) = Create(Token("old"), Token("new"));
        await service.TryAuthenticateAsync(Repo, "same-user", "same-password");
        await service.InvalidateTokenAsync(Repo);
        Assert.Equal("new", await service.GetTokenAsync(Repo));
        Assert.Equal(2, handler.Bodies.Count);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedCredentials_StopRepeatedPasswordAttempts(bool refresh)
    {
        var (service, handler) = Create(refresh
            ? [Token("old", true), Response(HttpStatusCode.Unauthorized), Response(HttpStatusCode.Unauthorized)]
            : [Token("old"), Response(HttpStatusCode.Unauthorized)]);
        await service.TryAuthenticateAsync(Repo, "same-user", "same-password");
        await service.InvalidateTokenAsync(Repo);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetTokenAsync(Repo));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetTokenAsync(Repo));
        Assert.Equal(refresh ? 3 : 2, handler.Bodies.Count);
    }
    [Fact]
    public async Task TemporaryRecoveryFailure_DoesNotPoisonTheSession()
    {
        var (service, handler) = Create(Token("old", true), Response(HttpStatusCode.Unauthorized),
            Response(HttpStatusCode.ServiceUnavailable), Token("recovered"));
        await service.TryAuthenticateAsync(Repo, "same-user", "same-password");
        await service.InvalidateTokenAsync(Repo);
        await Assert.ThrowsAsync<LaserficheReports.Domain.Exceptions.LaserficheException>(() => service.GetTokenAsync(Repo));
        Assert.Equal("recovered", await service.GetTokenAsync(Repo));
        Assert.Equal(4, handler.Bodies.Count);
    }
    private sealed class Handler(HttpResponseMessage[] responses) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);
        public List<string> Bodies { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Bodies.Add(await request.Content!.ReadAsStringAsync(ct)); return queue.Dequeue(); }
    }
    private sealed class Monitor(LaserficheOptions options) : IOptionsMonitor<LaserficheOptions>
    {
        public LaserficheOptions CurrentValue => options;
        public LaserficheOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<LaserficheOptions, string?> listener) => null;
    }
    private sealed class Credentials : ISessionCredentialStore
    {
        public Task<LaserficheCredential?> TryGetAsync(CancellationToken ct = default) => Task.FromResult<LaserficheCredential?>(new("same-user", "same-password"));
        public Task StoreAsync(string username, string password, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class ForbiddenDisk : ICredentialProvider
    {
        public Task<LaserficheCredential> GetCredentialsAsync(string key, CancellationToken ct = default) => throw new Exception("Disk credentials must never be used.");
        public Task StoreCredentialsAsync(string key, string username, string password, CancellationToken ct = default) => throw new NotImplementedException();
    }
    private sealed class Session : ISession
    {
        private readonly Dictionary<string, byte[]> values = [];
        public bool IsAvailable => true;
        public string Id => "reports-session";
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
    }
}
