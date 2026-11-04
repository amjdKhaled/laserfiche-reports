using System.Text;
using System.Text.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Web.Permissions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class PermissionPrincipalTests
{
    [Fact]
    public async Task SsoRequiresAnExactUniqueStableSubjectMappingNeverDisplayName()
    {
        var (resolver, http, auth, binding) = Fixture();
        http.Session.SetString("AuthenticationScopeMethod", "LFDS");
        http.Session.SetString("LfdsUser", "same-name");
        auth.Token = Token("issuer", "sub-A", "same-name");
        binding.Subjects = [new() { Issuer = "issuer", Subject = "sub-A", TrusteeSid = "sid-A" }];
        var a = await resolver.ResolveAsync(binding, default);
        Assert.Equal("sid-A", a!.TrusteeSid); Assert.Null(a.Credential);
        auth.Token = Token("issuer", "sub-B", "same-name");
        Assert.Null(await resolver.ResolveAsync(binding, default));
        binding.Subjects = [new() { Issuer = "issuer", Subject = "sub-B", TrusteeSid = "sid-B" }];
        var b = await resolver.ResolveAsync(binding, default);
        Assert.Equal("sid-B", b!.TrusteeSid); Assert.NotEqual(a.RateKey, b.RateKey);
        binding.Subjects = [binding.Subjects[0], binding.Subjects[0]];
        Assert.Null(await resolver.ResolveAsync(binding, default));
        auth.Token = Token("other-issuer", "sub-B", "same-name");
        Assert.Null(await resolver.ResolveAsync(binding, default));
    }
    [Fact]
    public async Task PasswordIdentityUsesServerCredentialsAndChangesScopeAcrossLogoutOrRepository()
    {
        var (resolver, http, auth, binding) = Fixture();
        http.Session.SetString("AuthenticationScopeMethod", "Reports");
        http.Session.SetString("test-user", "alice");
        var a = await resolver.ResolveAsync(binding, default);
        Assert.Equal("alice", a!.Credential!.Username); Assert.Null(a.TrusteeSid);
        http.Session.SetString("ReportsGeneration", "new-login");
        var renewed = await resolver.ResolveAsync(binding, default);
        Assert.NotEqual(a.Scope, renewed!.Scope); Assert.Equal(a.RateKey, renewed.RateKey);
        http.Session.SetString("repo", "B");
        Assert.NotEqual(renewed.Scope, (await resolver.ResolveAsync(binding, default))!.Scope);
        http.Session.Remove("test-user");
        Assert.Null(await resolver.ResolveAsync(binding, default));
        http.Session.SetString("test-user", "bob"); http.Session.Remove("AuthenticationScopeSubject");
        Assert.Null(await resolver.ResolveAsync(binding, default));
    }
    private static string Token(string issuer, string subject, string name) => "header." +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iss = issuer, sub = subject, name }))).TrimEnd('=').Replace('+','-').Replace('/','_') + ".signature";
    private static (AuthenticatedLaserfichePrincipalResolver, DefaultHttpContext, Auth, PermissionRepositoryBinding) Fixture()
    {
        var http = new DefaultHttpContext { Session = new Session() };
        http.Session.SetString("ReportsGeneration", "generation");
        http.Session.SetString("AuthenticationScopeSubject", "server-subject");
        var accessor = new HttpContextAccessor { HttpContext = http };
        var auth = new Auth();
        return (new(accessor, new Credentials(http), new Repositories(http), auth), http, auth, new());
    }
    private sealed class Session : ISession
    {
        private readonly Dictionary<string, byte[]> data = new();
        public string Id => "server-session"; public bool IsAvailable => true; public IEnumerable<string> Keys => data.Keys;
        public void Clear() => data.Clear(); public void Remove(string key) => data.Remove(key);
        public void Set(string key, byte[] value) => data[key] = value;
        public bool TryGetValue(string key, out byte[] value) => data.TryGetValue(key, out value!);
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Credentials(HttpContext http) : ISessionCredentialStore
    {
        public Task StoreAsync(string user, string password, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<LaserficheCredential?> TryGetAsync(CancellationToken ct = default) => Task.FromResult(
            http.Session.GetString("test-user") is { } user ? new LaserficheCredential(user, "fixture") : null);
        public async Task<string?> TryGetSignedInUserAsync(CancellationToken ct = default) =>
            http.Session.GetString("AuthenticationScopeMethod") == "LFDS" ? http.Session.GetString("LfdsUser") : (await TryGetAsync(ct))?.Username;
    }
    private sealed class Repositories(HttpContext http) : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) => Task.FromResult(new RepositoryDescriptor(http.Session.GetString("repo") ?? "A", "http://fixture", "A", "A"));
        public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) => [await GetActiveRepositoryAsync(ct)];
    }
    private sealed class Auth : ILaserficheAuthService
    {
        public string Token = "opaque";
        public Task<string> GetTokenAsync(RepositoryDescriptor r, CancellationToken ct = default) => Task.FromResult(Token);
        public Task InvalidateTokenAsync(RepositoryDescriptor r) => Task.CompletedTask;
        public Task InvalidateCurrentSessionTokensAsync() => Task.CompletedTask;
        public Task<bool> TryAuthenticateAsync(RepositoryDescriptor r, string u, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExchangeAuthorizationCodeAsync(RepositoryDescriptor r, string c, string v, string uri, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
