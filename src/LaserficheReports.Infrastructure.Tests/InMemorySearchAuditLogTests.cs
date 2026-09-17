using LaserficheReports.Infrastructure.Services;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

/// <summary>
/// Repository-isolation tests for the in-memory search audit log.
/// On a multi-repository server, search activity recorded for one repository
/// must never appear in another repository's dashboard statistics.
/// </summary>
public sealed class InMemorySearchAuditLogTests
{
    [Fact]
    public async Task TopQueries_AreIsolatedPerRepository()
    {
        var log = new InMemorySearchAuditLog();
        await log.RecordSearchAsync("RepoA", "invoices 2024");
        await log.RecordSearchAsync("RepoA", "invoices 2024");
        await log.RecordSearchAsync("RepoB", "personnel file");

        var topA = await log.GetTopQueriesAsync("RepoA");
        var topB = await log.GetTopQueriesAsync("RepoB");

        Assert.Single(topA);
        Assert.Equal("invoices 2024", topA[0].Query);
        Assert.Equal(2, topA[0].Count);

        Assert.Single(topB);
        Assert.Equal("personnel file", topB[0].Query);
        Assert.DoesNotContain(topB, q => q.Query.Contains("invoices"));
    }

    [Fact]
    public async Task TotalCount_IsIsolatedPerRepository()
    {
        var log = new InMemorySearchAuditLog();
        await log.RecordSearchAsync("RepoA", "q1");
        await log.RecordSearchAsync("RepoA", "q2");
        await log.RecordSearchAsync("RepoB", "q3");

        Assert.Equal(2, await log.GetTotalSearchCountAsync("RepoA"));
        Assert.Equal(1, await log.GetTotalSearchCountAsync("RepoB"));
        Assert.Equal(0, await log.GetTotalSearchCountAsync("RepoC"));
    }

    [Fact]
    public async Task SearchesByDay_CountsOnlyOwnRepository()
    {
        var log = new InMemorySearchAuditLog();
        await log.RecordSearchAsync("RepoA", "alpha");
        await log.RecordSearchAsync("RepoB", "beta");
        await log.RecordSearchAsync("RepoB", "gamma");

        var daysA = await log.GetSearchesByDayAsync("RepoA", days: 7);
        var daysB = await log.GetSearchesByDayAsync("RepoB", days: 7);

        Assert.Equal(1, daysA.Sum(d => d.Count));
        Assert.Equal(2, daysB.Sum(d => d.Count));
    }

    [Fact]
    public async Task RepositoryMatch_IsCaseInsensitiveAndTrimmed()
    {
        var log = new InMemorySearchAuditLog();
        await log.RecordSearchAsync("  RepoA  ", "query");

        Assert.Equal(1, await log.GetTotalSearchCountAsync("repoa"));
    }

    [Fact]
    public async Task Record_IgnoresBlankRepositoryOrQuery()
    {
        var log = new InMemorySearchAuditLog();
        await log.RecordSearchAsync("", "query");
        await log.RecordSearchAsync("RepoA", "   ");

        Assert.Equal(0, await log.GetTotalSearchCountAsync("RepoA"));
    }
}
