using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.OAuth;

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementation of <see cref="IOAuthStateStore"/>.
/// Entries expire after <see cref="StateLifetime"/> and can only be consumed once.
/// Registered as a singleton because <see cref="IMemoryCache"/> is singleton-safe.
/// </summary>
public sealed class OAuthStateStore : IOAuthStateStore
{
    /// <summary>Maximum age for a stored state entry.</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private const string KeyPrefix = "OAuthState:";

    private readonly IMemoryCache _cache;
    private readonly ILogger<OAuthStateStore> _logger;

    /// <summary>Initialises the store.</summary>
    public OAuthStateStore(IMemoryCache cache, ILogger<OAuthStateStore> logger)
    {
        _cache  = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Store(string state, OAuthStateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new ArgumentException("State must not be empty.", nameof(state));

        _cache.Set(KeyPrefix + state, entry, StateLifetime);

        _logger.LogDebug(
            "[SSO] OAuth state stored for repository {Repo}. Expires={Expires:o}",
            entry.RepositoryId,
            entry.ExpiresAt);
    }

    /// <inheritdoc />
    public OAuthStateEntry? TryConsume(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return null;

        var cacheKey = KeyPrefix + state;

        if (!_cache.TryGetValue(cacheKey, out OAuthStateEntry? entry) || entry is null)
        {
            // Log only a prefix — never log the full state token.
            _logger.LogWarning(
                "[SSO] State not found (expired, never issued, or already consumed): {StatePrefix}…",
                state.Length >= 8 ? state[..8] : state);
            return null;
        }

        if (entry.IsUsed)
        {
            _logger.LogWarning(
                "[SSO] Replay detected: state for repository {Repo} was already consumed.",
                entry.RepositoryId);
            return null;
        }

        if (DateTimeOffset.UtcNow >= entry.ExpiresAt)
        {
            _logger.LogWarning(
                "[SSO] State expired for repository {Repo}. Expired={Expired:o}",
                entry.RepositoryId,
                entry.ExpiresAt);
            _cache.Remove(cacheKey);
            return null;
        }

        // Mark consumed and evict atomically — a concurrent second request for the
        // same state key will find nothing in the cache.
        entry.IsUsed = true;
        _cache.Remove(cacheKey);

        _logger.LogDebug(
            "[SSO] OAuth state consumed for repository {Repo}.", entry.RepositoryId);

        return entry;
    }
}
