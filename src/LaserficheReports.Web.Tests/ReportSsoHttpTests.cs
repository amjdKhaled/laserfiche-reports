using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.OAuth;
using LaserficheReports.Infrastructure.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class ReportSsoHttpTests
{
    private const string Callback = "https://reports.test:5188/login/Callback";

    [Fact]
    public async Task StartSso_RedirectsToRepositoryApiAuthorizeWithPkce()
    {
        await using var host = await Host.Start();
        var location = await host.Redirect("/login/StartSso?repository=Demo");
        Assert.StartsWith("https://lf.test/LFRepositoryAPI/v2/Authorize?repositoryId=Demo&response_type=code&redirect_uri=" +
            Uri.EscapeDataString(Callback) + "&state=", location);
        Assert.Contains("&code_challenge_method=S256", location);
        Assert.DoesNotContain("prompt=login", location);
        Assert.EndsWith("&prompt=login", await host.Redirect("/login/StartSso?repository=Demo&forceLogin=true"));
    }

    [Fact]
    public async Task Callback_SignsInTheLfdsUserWithoutAPassword()
    {
        await using var host = await Host.Start();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/login", new { username = "password-user", password = "fixture", repositoryId = "A" })).StatusCode);
        var authorize = await host.Redirect("/login/StartSso?repository=Demo");
        Assert.Equal("/", await host.Redirect($"/login/Callback?code=fixture-code&state={Query(authorize, "state")}"));

        var status = await host.Status();
        Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.Equal("lfds-user", status.GetProperty("username").GetString());
        Assert.Equal("Demo", status.GetProperty("repository").GetString());
        Assert.True(status.GetProperty("sso").GetBoolean());
        Assert.Equal(1, host.Credentials.Clears); // The previous password is not kept for an SSO session.
        Assert.Equal("fixture-code", host.Auth.Code);
        Assert.Equal(Callback, host.Auth.RedirectUri);
        Assert.Equal(Query(authorize, "code_challenge"), Challenge(host.Auth.Verifier!));
    }

    [Fact]
    public async Task Callback_RejectsForgedAndReplayedStatesWithoutExchanging()
    {
        await using var host = await Host.Start();
        var state = Query(await host.Redirect("/login/StartSso?repository=Demo"), "state");
        Assert.Equal("/?sso_error=state", await host.Redirect("/login/Callback?code=fixture-code&state=forged"));
        Assert.Equal("/?sso_error=state", await host.Redirect($"/login/Callback?code=fixture-code&state={state}"));
        Assert.Equal(0, host.Auth.Exchanges);
        Assert.False((await host.Status()).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task FailedExchange_PreservesThePreviousSession()
    {
        await using var host = await Host.Start();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/login", new { username = "password-user", password = "fixture", repositoryId = "A" })).StatusCode);
        var before = (await host.Status()).ToString();
        host.Auth.Fail = true;
        var state = Query(await host.Redirect("/login/StartSso?repository=Demo"), "state");
        Assert.Equal("/?sso_error=exchange", await host.Redirect($"/login/Callback?code=fixture-code&state={state}"));
        Assert.Equal(before, (await host.Status()).ToString());
        Assert.Equal(1, host.Auth.Invalidations);
    }

    [Fact]
    public async Task ProviderError_ReturnsToTheLoginForm()
    {
        await using var host = await Host.Start();
        await host.Redirect("/login/StartSso?repository=Demo");
        Assert.Equal("/?sso_error=provider", await host.Redirect("/login/Callback?error=access_denied"));
        Assert.Equal(0, host.Auth.Exchanges);
    }

    [Theory]
    [InlineData(true, "/login/StartSso?repository=Demo")]
    [InlineData(false, "/?repository=Demo")]
    public async Task WebClientLaunch_EndsThePreviousIdentityOnlyForSso(bool sso, string expected)
    {
        await using var host = await Host.Start(sso);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/login", new { username = "password-user", password = "fixture", repositoryId = "A" })).StatusCode);
        Assert.Equal(expected, await host.Redirect("/launch?repository=Demo&source=webclient"));
        Assert.Equal(!sso, (await host.Status()).GetProperty("authenticated").GetBoolean());
    }

    private static string Query(string url, string name) => Uri.UnescapeDataString(
        new Uri(url).Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).Single(part => part[0] == name)[1]);

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public Auth Auth => app.Services.GetRequiredService<Auth>();
        public Credentials Credentials => app.Services.GetRequiredService<Credentials>();

        public async Task<string> Redirect(string url)
        {
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return response.Headers.Location!.OriginalString;
        }

        public async Task<JsonElement> Status() =>
            await client.GetFromJsonAsync<JsonElement>("/status");

        public static async Task<Host> Start(bool sso = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddMemoryCache();
            builder.Services.AddSession();
            builder.Services.AddHttpContextAccessor();
            builder.Services.Configure<LaserficheOptions>(options =>
            {
                options.ServerUrl = "https://lf.test";
                options.ApplicationBaseUrl = "https://reports.test:5188";
                options.AuthenticationMode = sso ? LaserficheAuthenticationMode.LfdsSso : LaserficheAuthenticationMode.RepositoryPassword;
                options.Sso.LfdsBaseUrl = "https://lf.test/LFDSSTS";
            });
            builder.Services.AddSingleton<IOAuthStateStore, OAuthStateStore>();
            builder.Services.AddSingleton<IRepositoryContext, Repositories>();
            builder.Services.AddSingleton<Credentials>();
            builder.Services.AddSingleton<ISessionCredentialStore>(sp => sp.GetRequiredService<Credentials>());
            builder.Services.AddSingleton<Auth>();
            builder.Services.AddSingleton<ILaserficheAuthService>(sp => sp.GetRequiredService<Auth>());
            var app = builder.Build();
            app.UseSession();
            app.MapPost("/login", ReportSessionEndpoints.LoginAsync);
            app.MapGet("/status", ReportSessionEndpoints.StatusAsync);
            app.MapReportSso();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
                { BaseAddress = new Uri(address) };
            return new Host(app, client);
        }

        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    private sealed class Credentials(IHttpContextAccessor accessor) : ISessionCredentialStore
    {
        private ISession Session => accessor.HttpContext!.Session;
        public int Clears;
        public Task StoreAsync(string username, string password, CancellationToken ct = default)
        { Session.SetString("test-user", username); Session.SetString("test-password", password); return Task.CompletedTask; }
        public Task<LaserficheCredential?> TryGetAsync(CancellationToken ct = default) => Task.FromResult(
            Session.GetString("test-user") is { } user ? new LaserficheCredential(user, Session.GetString("test-password")!) : null);
        public Task ClearAsync(CancellationToken ct = default)
        { Clears++; Session.Remove("test-user"); Session.Remove("test-password"); return Task.CompletedTask; }
        // Mirrors SessionCredentialStore: an LFDS session is identified by its token user.
        public Task<string?> TryGetSignedInUserAsync(CancellationToken ct = default) => Task.FromResult(
            Session.GetString("AuthenticationScopeMethod") == "LFDS" ? Session.GetString("LfdsUser") : Session.GetString("test-user"));
    }

    private sealed class Repositories(IHttpContextAccessor accessor) : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default)
        { var id = accessor.HttpContext!.Session.GetString("ActiveRepositoryId") ?? "A"; return Task.FromResult(new RepositoryDescriptor(id, "https://lf.test", id, id)); }
        public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) => [await GetActiveRepositoryAsync(ct)];
    }

    private sealed class Auth(IHttpContextAccessor accessor) : ILaserficheAuthService
    {
        public bool Fail;
        public int Exchanges, Invalidations;
        public string? Code, Verifier, RedirectUri;
        public Task<bool> TryAuthenticateAsync(RepositoryDescriptor repository, string username, string password, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExchangeAuthorizationCodeAsync(RepositoryDescriptor repository, string code, string codeVerifier, string redirectUri, string clientId, CancellationToken ct = default)
        {
            Exchanges++;
            (Code, Verifier, RedirectUri) = (code, codeVerifier, redirectUri);
            if (Fail) throw new LaserficheException("LFDS token exchange returned HTTP 400.", 400);
            accessor.HttpContext!.Session.SetString("AuthenticatedLaserficheUser", "lfds-user");
            return Task.FromResult(true);
        }
        public Task<string> GetTokenAsync(RepositoryDescriptor repository, CancellationToken ct = default) => Task.FromResult("fixture-access");
        public Task InvalidateCurrentSessionTokensAsync() { Invalidations++; return Task.CompletedTask; }
        public Task InvalidateTokenAsync(RepositoryDescriptor repository) => Task.CompletedTask;
    }
}
