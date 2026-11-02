using LaserficheReports.Application.DTOs;
using LaserficheReports.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class CachedAnalyticsScopeTests
{
    private static readonly RepositoryDescriptor Repository = new("A", "http://server", "A", "A");

    [Fact]
    public void PasswordAccountsNeverShareTheFallbackAnalyticsCache()
    {
        var first = Context("Reports", "user-a");
        var second = Context("Reports", "user-b");
        var keyA = CachedLaserficheAnalyticsService.SessionCacheKey(Repository, first);
        var keyB = CachedLaserficheAnalyticsService.SessionCacheKey(Repository, second);
        Assert.NotNull(keyA);
        Assert.NotNull(keyB);
        Assert.NotEqual(keyA, keyB);
        Assert.DoesNotContain("user-a", keyA!);
    }

    [Fact]
    public void NewLoginAndRepositorySwitchDoNotReusePreviousSnapshots()
    {
        var http = Context("LFDS", "identity");
        var initial = CachedLaserficheAnalyticsService.SessionCacheKey(Repository, http);
        http.Session.SetString("ReportsGeneration", "next-login");
        Assert.NotEqual(initial, CachedLaserficheAnalyticsService.SessionCacheKey(Repository, http));
        http.Session.SetString("ActiveRepositoryId", "B");
        Assert.Null(CachedLaserficheAnalyticsService.SessionCacheKey(Repository, http));
    }

    [Fact]
    public void UnboundOrUnauthenticatedRequestsCannotUseSharedSnapshot()
    {
        Assert.Null(CachedLaserficheAnalyticsService.SessionCacheKey(Repository, null));
        var http = Context("Reports", "identity");
        http.Session.Remove("AuthenticationScopeMethod");
        Assert.Null(CachedLaserficheAnalyticsService.SessionCacheKey(Repository, http));
        http.Session.SetString("AuthenticationScopeMethod", "Reports");
        http.Session.Remove("ReportsGeneration");
        Assert.Null(CachedLaserficheAnalyticsService.SessionCacheKey(Repository, http));
    }

    private static DefaultHttpContext Context(string method, string subject)
    {
        var http = new DefaultHttpContext { Session = new MemorySession() };
        http.Session.SetString("AuthenticationScopeMethod", method);
        http.Session.SetString("AuthenticationScopeSubject", subject);
        http.Session.SetString("ActiveRepositoryId", "A");
        http.Session.SetString("ReportsGeneration", "generation");
        return http;
    }

    private sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public bool IsAvailable => true;
        public string Id => "test-session";
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[] value)
        {
            if (values.TryGetValue(key, out var bytes)) { value = bytes; return true; }
            value = Array.Empty<byte>(); return false;
        }
    }
}
