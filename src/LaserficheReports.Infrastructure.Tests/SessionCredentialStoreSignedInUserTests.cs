using LaserficheReports.Infrastructure.Credentials;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class SessionCredentialStoreSignedInUserTests
{
    private static (SessionCredentialStore, ISession) Create()
    {
        var session = new Session();
        var store = new SessionCredentialStore(new HttpContextAccessor { HttpContext = new DefaultHttpContext { Session = session } },
            new EphemeralDataProtectionProvider(), NullLogger<SessionCredentialStore>.Instance);
        return (store, session);
    }

    [Fact]
    public async Task PasswordSession_IsSignedInAsTheStoredUser()
    {
        var (store, session) = Create();
        Assert.Null(await store.TryGetSignedInUserAsync());
        session.SetString("AuthenticationScopeMethod", "Reports");
        await store.StoreAsync("password-user", "fixture");
        Assert.Equal("password-user", await store.TryGetSignedInUserAsync());
    }

    [Fact]
    public async Task LfdsSession_IsSignedInAsTheTokenUserOnly()
    {
        var (store, session) = Create();
        await store.StoreAsync("stale-password-user", "fixture");
        session.SetString("AuthenticationScopeMethod", "LFDS");
        Assert.Null(await store.TryGetSignedInUserAsync()); // Not signed in until the callback commits.
        session.SetString("LfdsUser", "lfds-user");
        Assert.Equal("lfds-user", await store.TryGetSignedInUserAsync());
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
