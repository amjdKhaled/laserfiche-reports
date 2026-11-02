using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LaserficheReports.Web.Tests;

public sealed class RepositoryReadSessionTests
{
    private static readonly RepositoryDescriptor Repository = new("A", "http://server", "A", "A");

    [Fact]
    public async Task PasswordAccount_RequiresCompletedBoundSession()
    {
        var (http, store) = Setup();
        Assert.True(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));

        http.Session.Remove("ReportsGeneration");
        Assert.False(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
        http.Session.SetString("ReportsGeneration", "new");
        http.Session.SetString("ActiveRepositoryId", "B");
        Assert.False(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
    }

    [Fact]
    public async Task PasswordAccount_CannotReadAsDifferentStoredIdentity()
    {
        var (http, store) = Setup();
        store.DisplayUser = "other";
        Assert.False(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
        store.DisplayUser = "amjd";
        store.Credentials = null;
        Assert.False(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
    }

    [Fact]
    public async Task NoLoginScope_CannotReadEvenWithStoredCredentials()
    {
        var (http, store) = Setup();
        http.Session.Remove("AuthenticationScopeMethod");
        Assert.False(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
    }

    [Fact]
    public async Task LfdsPlaceholderIsNotAnAuthenticatedIdentity()
    {
        var (http, store) = Setup();
        http.Session.SetString("AuthenticationScopeMethod", "LFDS");
        http.Session.SetString("LfdsUser", "LFDS");
        store.DisplayUser = "LFDS";
        store.Credentials = null;
        Assert.False(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
        http.Session.SetString("AuthenticatedLaserficheUser", "amjd");
        http.Session.SetString("LfdsUser", "amjd");
        store.DisplayUser = "amjd";
        Assert.True(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
        http.Session.SetString("LfdsUser", "different");
        Assert.False(await RepositoryReadSession.IsAuthorizedAsync(http, Repository, store, default));
    }

    [Fact]
    public void AllDocumentAndReportRoutesRequireAuthenticatedSession()
    {
        foreach (var path in new[]
        {
            "/api/reports/chat", "/api/reports/documents", "/api/reports/laserfiche-links",
            "/api/laserfiche/documents/475/pages/1/image", "/api/laserfiche/repository",
            "/api/ingestion/laserfiche/475", "/api/recycle-bin/entries", "/api/audit-trail/reports"
        })
            Assert.True(RepositoryReadSession.IsRepositoryReadPath(new PathString(path)), path);
        Assert.False(RepositoryReadSession.IsRepositoryReadPath(new PathString("/api/session/login")));
        Assert.False(RepositoryReadSession.IsRepositoryReadPath(new PathString("/api/app/status")));
    }

    private static (DefaultHttpContext, Credentials) Setup()
    {
        var http = new DefaultHttpContext { Session = new MemorySession() };
        http.Session.SetString("AuthenticationScopeMethod", "Reports");
        http.Session.SetString("AuthenticationScopeSubject", "identity-1");
        http.Session.SetString("ActiveRepositoryId", "A");
        http.Session.SetString("ReportsGeneration", "generation-1");
        return (http, new Credentials { DisplayUser = "amjd", Credentials = new("amjd", "fixture") });
    }

    private sealed class Credentials : ISessionCredentialStore
    {
        public string? DisplayUser;
        public LaserficheCredential? Credentials;
        public Task StoreAsync(string username, string password, CancellationToken ct = default)
        { Credentials = new(username, password); DisplayUser = username; return Task.CompletedTask; }
        public Task<LaserficheCredential?> TryGetAsync(CancellationToken ct = default) =>
            Task.FromResult(Credentials);
        public Task<string?> TryGetSignedInUserAsync(CancellationToken ct = default) =>
            Task.FromResult(DisplayUser);
        public Task ClearAsync(CancellationToken ct = default)
        { DisplayUser = null; Credentials = null; return Task.CompletedTask; }
    }

    private sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
    }
}
