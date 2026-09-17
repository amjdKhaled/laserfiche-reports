using System.Collections.Concurrent;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// In-memory rolling audit log for portal search activity, scoped per repository.
/// Backed by a <see cref="ConcurrentQueue{T}"/> capped at <see cref="MaxCapacity"/> entries.
/// Data is not persisted across application restarts — this mirrors the original
/// GovSearch AI implementation which used a <c>MemStorage</c> in-memory store.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton so the log accumulates across the lifetime of the
/// application process. Thread-safe for concurrent read and write.
/// </para>
/// <para>
/// REPOSITORY ISOLATION: every entry carries the repository it was recorded for,
/// and every query filters to a single repository. This prevents search terms
/// typed by users of repository A from appearing in the dashboard statistics of
/// repository B on a multi-repository server.
/// </para>
/// </remarks>
internal sealed class InMemorySearchAuditLog : ISearchAuditLog
{
    private const int MaxCapacity = 10_000;

    private readonly ConcurrentQueue<SearchAuditEntry> _entries = new();

    /// <inheritdoc />
    public Task RecordSearchAsync(string repositoryId, string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(repositoryId))
            return Task.CompletedTask;

        _entries.Enqueue(new SearchAuditEntry(repositoryId.Trim(), query.Trim(), DateTimeOffset.UtcNow));

        // Trim to cap — dequeue oldest entries when over limit.
        // Count/TryDequeue are not atomic together, so under heavy concurrency
        // this may briefly over- or under-trim by a few entries; the queue
        // itself is never corrupted and the cap is approximate by design.
        while (_entries.Count > MaxCapacity)
        {
            _entries.TryDequeue(out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchActivityDayDto>> GetSearchesByDayAsync(
        string repositoryId,
        int days = 7,
        CancellationToken cancellationToken = default)
    {
        var now    = DateTimeOffset.UtcNow.Date;
        var cutoff = now.AddDays(-(days - 1));

        // Initialise all buckets with 0
        var buckets = new Dictionary<DateOnly, int>(days);
        for (var i = 0; i < days; i++)
        {
            buckets[DateOnly.FromDateTime(cutoff.AddDays(i))] = 0;
        }

        foreach (var entry in _entries)
        {
            if (!IsRepo(entry, repositoryId)) continue;
            var day = DateOnly.FromDateTime(entry.SearchedAt.LocalDateTime);
            if (buckets.ContainsKey(day))
                buckets[day]++;
        }

        var result = buckets
            .OrderBy(kv => kv.Key)
            .Select(kv => new SearchActivityDayDto
            {
                Date  = kv.Key.ToString("yyyy-MM-dd"),
                Label = kv.Key.ToString("MMM d"),
                Count = kv.Value
            })
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<SearchActivityDayDto>>(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TopQueryDto>> GetTopQueriesAsync(
        string repositoryId,
        int n = 5,
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (!IsRepo(entry, repositoryId)) continue;
            var key = entry.Query.ToLowerInvariant();
            counts[key] = (counts.TryGetValue(key, out var c) ? c : 0) + 1;
        }

        var top = counts
            .OrderByDescending(kv => kv.Value)
            .Take(n)
            .Select(kv => new TopQueryDto { Query = kv.Key, Count = kv.Value })
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<TopQueryDto>>(top);
    }

    /// <inheritdoc />
    public Task<int> GetTotalSearchCountAsync(string repositoryId, CancellationToken cancellationToken = default)
        => Task.FromResult(_entries.Count(e => IsRepo(e, repositoryId)));

    // Laserfiche repository names are case-insensitive identifiers.
    private static bool IsRepo(SearchAuditEntry entry, string repositoryId) =>
        string.Equals(entry.RepositoryId, repositoryId?.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Private record ─────────────────────────────────────────────────────

    private sealed record SearchAuditEntry(string RepositoryId, string Query, DateTimeOffset SearchedAt);
}
