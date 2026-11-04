using System.Net;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class BearerTokenHandlerTests
{
    private const string ClosedSession =
        "{\"type\":\"noConnection\",\"title\":\"The object must have its connection set before using it.\",\"status\":404}";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.NotFound, ClosedSession)]
    public async Task ClosedLaserficheSession_IsRenewedAndTheRequestRepeatedOnce(HttpStatusCode status, string body)
    {
        var auth = new Auth();
        var server = new Server((status, body), (HttpStatusCode.OK, "{\"id\":1}"));
        using var client = Client(auth, server);
        using var response = await client.GetAsync("https://lf.test/LFRepositoryAPI/v2/Repositories/Demo/Entries/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], server.Authorizations);
        Assert.Equal(["token-1"], auth.Invalidated);
    }

    [Fact]
    public async Task MissingEntry_IsNotMistakenForAClosedSession()
    {
        var auth = new Auth();
        var server = new Server((HttpStatusCode.NotFound, "{\"type\":\"notFound\",\"title\":\"Entry not found.\"}"));
        using var client = Client(auth, server);
        using var response = await client.GetAsync("https://lf.test/LFRepositoryAPI/v2/Repositories/Demo/Entries/999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Entry not found.", await response.Content.ReadAsStringAsync());
        Assert.Single(server.Authorizations);
        Assert.Empty(auth.Invalidated);
    }

    private static HttpClient Client(Auth auth, Server server) =>
        new(new BearerTokenHandler(auth, new Repositories(), NullLogger<BearerTokenHandler>.Instance) { InnerHandler = server });

    private sealed class Server(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> queue = new(responses);
        public List<string> Authorizations { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorizations.Add(request.Headers.Authorization!.ToString());
            var (status, body) = queue.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Repositories : IRepositoryContext
    {
        private static readonly RepositoryDescriptor Demo = new("Demo", "https://lf.test", "Demo", "Demo");
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) => Task.FromResult(Demo);
        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryDescriptor>>([Demo]);
    }

    private sealed class Auth : ILaserficheAuthService
    {
        private int issued;
        public List<string> Invalidated { get; } = [];
        public Task<string> GetTokenAsync(RepositoryDescriptor repository, CancellationToken ct = default) =>
            Task.FromResult("token-" + Interlocked.Increment(ref issued));
        public Task InvalidateTokenAsync(RepositoryDescriptor repository) => Task.CompletedTask;
        public Task InvalidateTokenAsync(RepositoryDescriptor repository, string rejectedToken)
        { Invalidated.Add(rejectedToken); return Task.CompletedTask; }
        public Task InvalidateCurrentSessionTokensAsync() => Task.CompletedTask;
        public Task<bool> TryAuthenticateAsync(RepositoryDescriptor repository, string username, string password, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<bool> ExchangeAuthorizationCodeAsync(RepositoryDescriptor repository, string code, string codeVerifier,
            string redirectUri, string clientId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
