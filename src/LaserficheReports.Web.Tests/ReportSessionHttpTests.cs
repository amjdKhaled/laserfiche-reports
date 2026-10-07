using System.Net;
using System.Net.Http.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class ReportSessionHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAccountSwitch_PreservesPreviousSessionOverHttp(bool gatewayFailure)
    {
        await using var host = await Host.Start();
        var first = await host.Client.PostAsJsonAsync("/login", new { username = "first", password = "fixture", repositoryId = "A" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var previous = await host.Client.GetStringAsync("/state");
        host.Auth.Reject = !gatewayFailure;
        host.Auth.Fail = gatewayFailure;
        var failed = await host.Client.PostAsJsonAsync("/login", new { username = "second", password = "fixture", repositoryId = "B" });
        Assert.Equal(gatewayFailure ? HttpStatusCode.BadGateway : HttpStatusCode.Unauthorized, failed.StatusCode);
        Assert.Equal(previous, await host.Client.GetStringAsync("/state"));
        Assert.Equal(1, host.Auth.Invalidations);
        Assert.NotEqual(host.Auth.FirstScope, host.Auth.InvalidatedScope);
        host.Auth.Reject = host.Auth.Fail = false;
        var reuse = await host.Client.PostAsJsonAsync("/login", new { username = "first", password = "fixture", repositoryId = "A" });
        Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
        Assert.Equal(2, host.Auth.Authentications); // Failed switch only; existing account reuses its token.
        Assert.Equal(1, host.Auth.Reads);
        Assert.Equal(previous, await host.Client.GetStringAsync("/state"));
    }

    [Fact]
    public async Task SuccessfulAccountSwitch_UsesNewIsolatedScopeAndCredentials()
    {
        await using var host = await Host.Start();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/login", new { username = "first", password = "fixture", repositoryId = "A" })).StatusCode);
        var previous = await host.Client.GetStringAsync("/state");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/login", new { username = "second", password = "fixture", repositoryId = "B" })).StatusCode);
        var state = await host.Client.GetStringAsync("/state");
        Assert.NotEqual(previous, state);
        Assert.Contains("second", state);
        Assert.Contains("B", state);
        Assert.NotEqual(host.Auth.FirstScope, host.Auth.LastScope);
    }

    private sealed class Host(WebApplication app, HttpClient client, Auth auth) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public Auth Auth => auth;
        public static async Task<Host> Start()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSession();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IRepositoryContext, Repositories>();
            builder.Services.AddSingleton<ISessionCredentialStore, Credentials>();
            builder.Services.AddSingleton<Auth>();
            builder.Services.AddSingleton<ILaserficheAuthService>(sp => sp.GetRequiredService<Auth>());
            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                try { await next(http); }
                catch (LaserficheException) { http.Response.StatusCode = 502; }
            });
            app.UseSession();
            app.MapPost("/login", ReportSessionEndpoints.LoginAsync);
            app.MapGet("/state", async (HttpContext http, ISessionCredentialStore credentials) =>
                new { user = (await credentials.TryGetAsync())?.Username,
                    repo = http.Session.GetString("ActiveRepositoryId"),
                    scope = http.Session.GetString("AuthenticationScopeSubject"),
                    generation = http.Session.GetString("ReportsGeneration") });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient(new HttpClientHandler { UseProxy = false, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address) };
            return new Host(app, client, app.Services.GetRequiredService<Auth>());
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    private sealed class Credentials(IHttpContextAccessor accessor) : ISessionCredentialStore
    {
        public Task StoreAsync(string username, string password, CancellationToken ct = default)
        { accessor.HttpContext!.Session.SetString("test-user", username); accessor.HttpContext.Session.SetString("test-password", password); return Task.CompletedTask; }
        public Task<LaserficheCredential?> TryGetAsync(CancellationToken ct = default) => Task.FromResult(
            accessor.HttpContext!.Session.GetString("test-user") is { } user ? new LaserficheCredential(user, accessor.HttpContext.Session.GetString("test-password")!) : null);
        public Task ClearAsync(CancellationToken ct = default)
        { accessor.HttpContext!.Session.Remove("test-user"); accessor.HttpContext.Session.Remove("test-password"); return Task.CompletedTask; }
    }
    private sealed class Repositories(IHttpContextAccessor accessor) : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default)
        { var id = accessor.HttpContext!.Session.GetString("ActiveRepositoryId") ?? "A"; return Task.FromResult(new RepositoryDescriptor(id, "http://fixture", id, id)); }
        public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) => [await GetActiveRepositoryAsync(ct)];
    }
    private sealed class Auth(IHttpContextAccessor accessor) : ILaserficheAuthService
    {
        public bool Reject, Fail;
        public int Authentications, Reads, Invalidations;
        public string? FirstScope, LastScope, InvalidatedScope;
        public Task<bool> TryAuthenticateAsync(RepositoryDescriptor repository, string username, string password, CancellationToken ct = default)
        {
            Authentications++;
            LastScope = accessor.HttpContext!.Session.GetString("AuthenticationScopeSubject");
            FirstScope ??= LastScope;
            if (Fail) throw new LaserficheException("fixture gateway failure", 503);
            return Task.FromResult(!Reject);
        }
        public Task<string> GetTokenAsync(RepositoryDescriptor repository, CancellationToken ct = default) { Reads++; return Task.FromResult("fixture-access"); }
        public Task InvalidateCurrentSessionTokensAsync() { Invalidations++; InvalidatedScope = accessor.HttpContext!.Session.GetString("AuthenticationScopeSubject"); return Task.CompletedTask; }
        public Task InvalidateTokenAsync(RepositoryDescriptor repository) => Task.CompletedTask;
        public Task<bool> ExchangeAuthorizationCodeAsync(RepositoryDescriptor repository, string code, string codeVerifier, string redirectUri, string clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
