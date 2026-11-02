using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Infrastructure.Credentials;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class SessionCredentialFallbackTests
{
    [Theory]
    [InlineData("Reports")]
    [InlineData("LFDS")]
    public async Task InteractiveSessionNeverUsesMachineAccountWhenSessionPasswordIsMissing(string method)
    {
        var (provider, disk, _, _) = Setup(method);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.GetCredentialsAsync("repository"));
        Assert.Equal(0, disk.ReadCount);
    }

    [Fact]
    public async Task SsoCannotUseStaleRepositoryPasswordFromPreviousSignIn()
    {
        var (provider, disk, session, store) = Setup("LFDS");
        store.Credential = new("old-account", "fixture");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.GetCredentialsAsync("repository"));
        Assert.Equal(0, disk.ReadCount);
    }

    [Fact]
    public async Task SignedOutSessionWithGenerationCannotReadMachineCredentials()
    {
        var (provider, disk, session, _) = Setup(null);
        session.SetString("ReportsGeneration", "signed-out");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.GetCredentialsAsync("repository"));
        Assert.Equal(0, disk.ReadCount);
    }

    [Fact]
    public async Task ExplicitPasswordLoginKeepsItsOwnCredential()
    {
        var (provider, disk, _, store) = Setup("Reports");
        store.Credential = new("amjd", "fixture");
        var value = await provider.GetCredentialsAsync("repository");
        Assert.Equal("amjd", value.Username);
        Assert.Equal(0, disk.ReadCount);
    }

    private static (SessionAwareCredentialProvider, Disk, MemorySession, Stored) Setup(string? method)
    {
        var session = new MemorySession();
        if (method != null) session.SetString("AuthenticationScopeMethod", method);
        var contexts = new HttpContextAccessor { HttpContext = new DefaultHttpContext { Session = session } };
        var disk = new Disk(); var store = new Stored();
        var provider = new SessionAwareCredentialProvider(store, disk, contexts,
            NullLogger<SessionAwareCredentialProvider>.Instance);
        return (provider, disk, session, store);
    }

    private sealed class Stored : ISessionCredentialStore
    {
        public LaserficheCredential? Credential;
        public Task<LaserficheCredential?> TryGetAsync(CancellationToken ct = default) =>
            Task.FromResult(Credential);
        public Task StoreAsync(string username, string password, CancellationToken ct = default)
        { Credential = new(username, password); return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default)
        { Credential = null; return Task.CompletedTask; }
    }

    private sealed class Disk : ICredentialProvider
    {
        public int ReadCount;
        public Task<LaserficheCredential> GetCredentialsAsync(string key, CancellationToken ct = default)
        { ReadCount++; return Task.FromResult(new LaserficheCredential("machine-admin", "fixture")); }
        public Task StoreCredentialsAsync(string key, string username, string password, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> data = new();
        public bool IsAvailable => true;
        public string Id => "session";
        public IEnumerable<string> Keys => data.Keys;
        public void Clear() => data.Clear();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => data.Remove(key);
        public void Set(string key, byte[] value) => data[key] = value;
        public bool TryGetValue(string key, out byte[] value)
        {
            if (data.TryGetValue(key, out var bytes)) { value = bytes; return true; }
            value = Array.Empty<byte>(); return false;
        }
    }
}
