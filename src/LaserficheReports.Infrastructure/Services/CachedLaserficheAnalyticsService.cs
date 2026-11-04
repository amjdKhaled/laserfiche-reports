using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Keeps a compact, permission-scoped repository snapshot. Repeated page opens do not
/// recursively enumerate the repository again, and large entry arrays are not retained.
/// </summary>
internal sealed class CachedLaserficheAnalyticsService : ILaserficheAnalyticsService, IAnalyticsCacheControl
{
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private const int AnalyticsRowLimit = 100;

    private readonly LaserficheAnalyticsService _inner;
    private readonly IRepositoryContext _repositoryContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedLaserficheAnalyticsService> _logger;

    public CachedLaserficheAnalyticsService(
        LaserficheAnalyticsService inner,
        IRepositoryContext repositoryContext,
        IHttpContextAccessor httpContextAccessor,
        IMemoryCache cache,
        ILogger<CachedLaserficheAnalyticsService> logger)
    {
        _inner = inner;
        _repositoryContext = repositoryContext;
        _httpContextAccessor = httpContextAccessor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<RepositoryStatsDto> GetRepositoryStatsAsync(CancellationToken cancellationToken = default)
    {
        var key = await GetCacheKeyAsync(cancellationToken).ConfigureAwait(false);
        // Never use a shared "fallback" identity for user-scoped repository
        // snapshots. Outside a verified browser session, read live without caching.
        if (key is null) return await _inner.GetRepositoryStatsAsync(cancellationToken).ConfigureAwait(false);
        if (_cache.TryGetValue(key, out RepositoryStatsDto? cached) && cached is not null)
        {
            _logger.LogInformation("Repository snapshot cache hit for {CacheKey}.", key);
            return cached;
        }

        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var live = await _inner.GetRepositoryStatsAsync(cancellationToken).ConfigureAwait(false);
            if (!live.IsConnected)
                return live;

            var compact = Compact(live);
            _cache.Set(key, compact, SnapshotLifetime);
            return compact;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        var key = await GetCacheKeyAsync(cancellationToken).ConfigureAwait(false);
        if (key is not null) _cache.Remove(key);
    }

    private async Task<string?> GetCacheKeyAsync(CancellationToken cancellationToken)
    {
        var repository = await _repositoryContext.GetActiveRepositoryAsync(cancellationToken).ConfigureAwait(false);
        return SessionCacheKey(repository, _httpContextAccessor.HttpContext);
    }

    // Password-grant users do not necessarily set HttpContext.User claims. They
    // MUST NOT share cached statistics under the old "fallback:fallback" key.
    // Scope to the server-issued session generation and identity, not a client
    // supplied username; after logout/relogin the key changes even for one user.
    internal static string? SessionCacheKey(RepositoryDescriptor repository, HttpContext? context)
    {
        if (context is null) return null;
        try
        {
            var session = context.Session;
            var method = session.GetString("AuthenticationScopeMethod");
            var subject = session.GetString("AuthenticationScopeSubject");
            var generation = session.GetString("ReportsGeneration");
            var id = session.GetString("ActiveRepositoryId");
            if (method is not ("Reports" or "LFDS") || string.IsNullOrWhiteSpace(subject) ||
                string.IsNullOrWhiteSpace(generation) ||
                !string.Equals(repository.RepositoryId, id, StringComparison.OrdinalIgnoreCase))
                return null;
            var input = string.Join("\\n", repository.ServerUrl.TrimEnd('/').ToLowerInvariant(),
                repository.RepositoryId.ToUpperInvariant(), method, session.Id, subject, generation);
            return "reports:v2:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        }
        catch (InvalidOperationException)
        {
            return null; // Unavailable session middleware is never a cache identity.
        }
    }

    internal static RepositoryStatsDto Compact(RepositoryStatsDto source)
    {
        var recent = source.RecentDocs.Take(AnalyticsRowLimit).ToList().AsReadOnly();
        var modified = source.ModifiedDocs.Take(AnalyticsRowLimit).ToList().AsReadOnly();
        var rootFolders = source.RootFolders.Select(item => item with { DocumentIds = [] }).ToList().AsReadOnly();
        return source with
        {
            AllDocs = [],
            AllFolders = [],
            RootFolders = rootFolders,
            RecentDocs = recent,
            ModifiedDocs = modified,
            RecentDocuments = recent,
            RecentlyIndexedDocuments = recent,
            RecentEntries = source.RecentEntries.Take(10).ToList().AsReadOnly()
        };
    }
}
