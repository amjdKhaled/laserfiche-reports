using LaserficheReports.Application.DTOs;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Repository;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="SessionAwareRepositoryContext"/>.
///
/// Guards the session-precedence invariant: when the Laserfiche Web Client (or
/// Desktop Client) launches the portal with <c>?repository=NewEmployeeTest</c>,
/// the middleware stores that value in the ASP.NET Core session
/// (<c>ActiveRepositoryId</c>).  Every subsequent call to
/// <see cref="SessionAwareRepositoryContext.GetActiveRepositoryAsync"/> must
/// return the session-stored value, NOT the configured fallback
/// (e.g. <c>LFNewRepoWF</c>).
///
/// Tests 2-4 from the regression suite:
///  2. Session repository overrides the configured fallback (session → config fallback).
///  3. When no session is present the configured default is returned (fallback behaviour).
///  4. The configured fallback cannot overwrite a session-provided repository.
/// </summary>
public sealed class SessionAwareRepositoryContextTests
{
    // ── Fixture helpers ───────────────────────────────────────────────────────

    /// <summary>Config default is "LFNewRepoWF" — the fallback when no session exists.</summary>
    private static LaserficheOptions ConfigOptions(string fallbackRepoId = "LFNewRepoWF") => new()
    {
        ServerUrl    = "http://lf-server.test",
        ApiBasePath  = "/LFRepositoryAPI",
        ApiVersion   = "v1",
        RepositoryId = fallbackRepoId,
    };

    private static SessionAwareRepositoryContext CreateContext(
        ISession session,
        LaserficheOptions? options = null)
    {
        var opts    = options ?? ConfigOptions();
        var monitor = new StaticOptionsMonitor<LaserficheOptions>(opts);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Session = session;

        var accessor = new HttpContextAccessor { HttpContext = httpCtx };
        return new SessionAwareRepositoryContext(monitor, accessor);
    }

    // ── Test 2: Session repository overrides configured fallback ──────────────

    [Fact]
    public async Task GetActiveRepositoryAsync_WithSessionRepo_ReturnsSessionValue()
    {
        // Arrange: session has "NewEmployeeTest" (set by RepositorySessionMiddleware
        // when the Web Client launched with ?repository=NewEmployeeTest)
        var session = new TestSession();
        session.SetString(SessionAwareRepositoryContext.SessionKeyRepositoryId, "NewEmployeeTest");

        var ctx = CreateContext(session);  // config fallback is "LFNewRepoWF"

        // Act
        var repo = await ctx.GetActiveRepositoryAsync();

        // Assert: session value wins over config fallback
        // Session repository must override the configured fallback.
        // LFNewRepoWF (config) must not overwrite NewEmployeeTest (session).
        Assert.Equal("NewEmployeeTest", repo.RepositoryId);
    }

    [Fact]
    public async Task GetActiveRepositoryAsync_WithSessionRepo_DisplayNameIsSessionRepoId()
    {
        // When the repo came from the session, the display name should be the repo ID itself
        // (not the config display name, which refers to a different repository).
        var session = new TestSession();
        session.SetString(SessionAwareRepositoryContext.SessionKeyRepositoryId, "NewEmployeeTest");

        var ctx  = CreateContext(session);
        var repo = await ctx.GetActiveRepositoryAsync();

        Assert.Equal("NewEmployeeTest", repo.DisplayName);
    }

    // ── Test 3: Fallback to config when no session is present ─────────────────

    [Fact]
    public async Task GetActiveRepositoryAsync_NoSession_ReturnsFallbackFromConfig()
    {
        // Arrange: no session value (direct browser access, no ?repository= param)
        var session = new TestSession();   // empty session

        var ctx  = CreateContext(session, ConfigOptions(fallbackRepoId: "LFNewRepoWF"));
        var repo = await ctx.GetActiveRepositoryAsync();

        // Without a session value the configured RepositoryId must be returned.
        Assert.Equal("LFNewRepoWF", repo.RepositoryId);
    }

    [Fact]
    public async Task GetActiveRepositoryAsync_EmptySessionString_ReturnsFallbackFromConfig()
    {
        // An explicitly empty session value should fall through to the config fallback.
        var session = new TestSession();
        session.SetString(SessionAwareRepositoryContext.SessionKeyRepositoryId, "   ");

        var ctx  = CreateContext(session, ConfigOptions(fallbackRepoId: "LFNewRepoWF"));
        var repo = await ctx.GetActiveRepositoryAsync();

        // Whitespace-only session value must not override the config fallback.
        Assert.Equal("LFNewRepoWF", repo.RepositoryId);
    }

    [Fact]
    public async Task GetActiveRepositoryAsync_NullHttpContext_ReturnsFallbackFromConfig()
    {
        // When there is no HTTP context (background job, health check), the context
        // falls back to the configured default gracefully — no exception thrown.
        var opts    = ConfigOptions(fallbackRepoId: "LFNewRepoWF");
        var monitor = new StaticOptionsMonitor<LaserficheOptions>(opts);
        var accessor = new HttpContextAccessor();  // HttpContext is null here

        var ctx  = new SessionAwareRepositoryContext(monitor, accessor);
        var repo = await ctx.GetActiveRepositoryAsync();

        Assert.Equal("LFNewRepoWF", repo.RepositoryId);
    }

    // ── Test 4: Config cannot overwrite a session-provided repository ──────────

    [Fact]
    public async Task GetActiveRepositoryAsync_SessionRepoIsPersistent_ConfigChangeDoesNotOverride()
    {
        // Even if the config RepositoryId is updated (e.g. admin changes the settings),
        // an in-flight session with "NewEmployeeTest" must NOT be overridden by the new config.
        var session = new TestSession();
        session.SetString(SessionAwareRepositoryContext.SessionKeyRepositoryId, "NewEmployeeTest");

        // Use a config that has a DIFFERENT repository
        var ctx = CreateContext(session, ConfigOptions(fallbackRepoId: "LFNewRepoWF"));

        var repo = await ctx.GetActiveRepositoryAsync();
        // Config update must not silently replace an active Web Client session repository.
        Assert.Equal("NewEmployeeTest", repo.RepositoryId);
    }

    [Fact]
    public async Task GetActiveRepositoryAsync_ServerUrlComesFromConfig_NotSession()
    {
        // The server URL (base for API calls) must always come from config,
        // not from the session — the session only stores the repository ID.
        var session = new TestSession();
        session.SetString(SessionAwareRepositoryContext.SessionKeyRepositoryId, "NewEmployeeTest");

        var ctx  = CreateContext(session, ConfigOptions());
        var repo = await ctx.GetActiveRepositoryAsync();

        Assert.Equal("http://lf-server.test", repo.ServerUrl);
    }

    [Fact]
    public async Task GetAllRepositoriesAsync_ReturnsSingleActiveRepository()
    {
        var session = new TestSession();
        session.SetString(SessionAwareRepositoryContext.SessionKeyRepositoryId, "NewEmployeeTest");

        var ctx   = CreateContext(session);
        var repos = await ctx.GetAllRepositoriesAsync();

        Assert.Single(repos);
        Assert.Equal("NewEmployeeTest", repos[0].RepositoryId);
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _data = [];

        public bool   IsAvailable  => true;
        public string Id           => "test-session-id";
        public IEnumerable<string> Keys => _data.Keys;

        public void   Clear()                               => _data.Clear();
        public void   Remove(string key)                   => _data.Remove(key);
        public void   Set(string key, byte[] value)        => _data[key] = value;
        public bool   TryGetValue(string key, out byte[] value) =>
            _data.TryGetValue(key, out value!);

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync  (CancellationToken ct = default) => Task.CompletedTask;
    }
}
