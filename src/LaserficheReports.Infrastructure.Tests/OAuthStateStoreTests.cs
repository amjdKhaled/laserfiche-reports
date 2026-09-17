using LaserficheReports.Infrastructure.OAuth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="OAuthStateStore"/>: storage, retrieval, expiry, and
/// anti-replay protection.
/// </summary>
public sealed class OAuthStateStoreTests
{
    // ── Factory ───────────────────────────────────────────────────────────────

    private static OAuthStateStore CreateStore() =>
        new(new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OAuthStateStore>.Instance);

    private static OAuthStateEntry MakeEntry(
        string repositoryId = "TestRepo",
        string returnUrl    = "/Chat",
        DateTimeOffset? expiresAt = null) =>
        new()
        {
            RepositoryId = repositoryId,
            ReturnUrl    = returnUrl,
            CodeVerifier = "test-verifier",
            RedirectUri  = "https://host/Login/Callback",
            ExpiresAt    = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10),
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Store and consume — happy path
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryConsume_AfterStore_ReturnsEntry()
    {
        var store = CreateStore();
        var entry = MakeEntry();

        store.Store("my-state-key", entry);
        var result = store.TryConsume("my-state-key");

        Assert.NotNull(result);
        Assert.Equal("TestRepo",       result.RepositoryId);
        Assert.Equal("/Chat",     result.ReturnUrl);
        Assert.Equal("test-verifier",  result.CodeVerifier);
    }

    [Fact]
    public void TryConsume_PreservesAllFields()
    {
        var store   = CreateStore();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var entry   = new OAuthStateEntry
        {
            RepositoryId = "Repo1",
            ReturnUrl    = "/Archive",
            CodeVerifier = "verifier-abc",
            RedirectUri  = "https://dash/Login/Callback",
            ExpiresAt    = expires,
        };

        store.Store("key1", entry);
        var result = store.TryConsume("key1");

        Assert.NotNull(result);
        Assert.Equal("Repo1",                     result.RepositoryId);
        Assert.Equal("/Archive",                  result.ReturnUrl);
        Assert.Equal("verifier-abc",              result.CodeVerifier);
        Assert.Equal("https://dash/Login/Callback", result.RedirectUri);
        Assert.Equal(expires,                     result.ExpiresAt);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unknown key
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryConsume_UnknownKey_ReturnsNull()
    {
        var store = CreateStore();
        var result = store.TryConsume("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public void TryConsume_EmptyKey_ReturnsNull()
    {
        var store = CreateStore();
        Assert.Null(store.TryConsume(""));
        Assert.Null(store.TryConsume("   "));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Anti-replay: second consume returns null
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryConsume_SecondCall_ReturnsNull_AntiReplay()
    {
        var store = CreateStore();
        store.Store("state-xyz", MakeEntry());

        var first  = store.TryConsume("state-xyz");
        var second = store.TryConsume("state-xyz");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryConsume_ThirdCall_AlsoReturnsNull()
    {
        var store = CreateStore();
        store.Store("state-abc", MakeEntry());

        store.TryConsume("state-abc"); // first (consumed)
        store.TryConsume("state-abc"); // second (replay denied)
        var third = store.TryConsume("state-abc");

        Assert.Null(third);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Expired entries
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryConsume_ExpiredEntry_ReturnsNull()
    {
        var store = CreateStore();
        // Entry expired one second ago.
        var expired = MakeEntry(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        store.Store("expired-state", expired);
        var result = store.TryConsume("expired-state");

        Assert.Null(result);
    }

    [Fact]
    public void TryConsume_JustExpired_ReturnsNull()
    {
        var store   = CreateStore();
        var expired = MakeEntry(expiresAt: DateTimeOffset.UtcNow);

        store.Store("edge-state", expired);
        var result = store.TryConsume("edge-state");

        Assert.Null(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Multiple independent entries
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoDistinctKeys_AreIndependent()
    {
        var store = CreateStore();
        store.Store("state-a", MakeEntry("RepoA", "/A"));
        store.Store("state-b", MakeEntry("RepoB", "/B"));

        var a = store.TryConsume("state-a");
        var b = store.TryConsume("state-b");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("RepoA", a!.RepositoryId);
        Assert.Equal("RepoB", b!.RepositoryId);
    }

    [Fact]
    public void ConsumingA_DoesNotAffectB()
    {
        var store = CreateStore();
        store.Store("state-a", MakeEntry("RepoA"));
        store.Store("state-b", MakeEntry("RepoB"));

        store.TryConsume("state-a"); // consume A

        var b = store.TryConsume("state-b"); // B should still be available
        Assert.NotNull(b);
        Assert.Equal("RepoB", b!.RepositoryId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Store with empty state string throws
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Store_EmptyState_Throws()
    {
        var store = CreateStore();
        Assert.Throws<ArgumentException>(() => store.Store("", MakeEntry()));
    }

    [Fact]
    public void Store_WhitespaceState_Throws()
    {
        var store = CreateStore();
        Assert.Throws<ArgumentException>(() => store.Store("   ", MakeEntry()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Overwrite existing key
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Store_OverwritesExisting_ReturnsNewest()
    {
        var store = CreateStore();
        store.Store("state", MakeEntry("OldRepo"));
        store.Store("state", MakeEntry("NewRepo")); // overwrite

        var result = store.TryConsume("state");

        Assert.NotNull(result);
        Assert.Equal("NewRepo", result!.RepositoryId);
    }
}
