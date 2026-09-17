using System.Collections.Concurrent;
using System.Security.Claims;
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

    public async Task InvalidateAsync(CancellationToken cancellationToken = default) =>
        _cache.Remove(await GetCacheKeyAsync(cancellationToken).ConfigureAwait(false));

    private async Task<string> GetCacheKeyAsync(CancellationToken cancellationToken)
    {
        var repository = await _repositoryContext.GetActiveRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var principal = _httpContextAccessor.HttpContext?.User;
        var user = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal?.Identity?.Name
            ?? "fallback";
        var auth = principal?.FindFirst(ClaimTypes.AuthenticationMethod)?.Value ?? "fallback";
        return $"reports:v1:{repository.ServerUrl}:{repository.RepositoryId}:{auth}:{user}";
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
