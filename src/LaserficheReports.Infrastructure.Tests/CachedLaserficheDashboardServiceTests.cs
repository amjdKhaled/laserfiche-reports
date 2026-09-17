using LaserficheReports.Application.DTOs;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Infrastructure.Services;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class CachedLaserficheDashboardServiceTests
{
    [Fact]
    public void Compact_ReleasesLargeEntryCollectionsButPreservesExactSummaries()
    {
        var activity = new[]
        {
            new DocumentActivityDayDto
            {
                Date = new DateOnly(2026, 9, 14),
                Created = 1_250,
                Modified = 340
            }
        };
        var users = new[]
        {
            new UserDocumentActivityDto
            {
                Name = "ADMIN",
                Created = 1_250,
                LastActivity = new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.Zero)
            }
        };
        var documents = Enumerable.Range(1, 150)
            .Select(id => new LFEntry { Id = id, Name = $"Document {id}", EntryType = LFEntryType.Document })
            .ToList();

        var compact = CachedLaserficheDashboardService.Compact(new DashboardStatsDto
        {
            IsConnected = true,
            TotalDocuments = 1_250,
            AllDocs = documents,
            AllFolders = [new LFEntry { Id = 500, Name = "Folder", EntryType = LFEntryType.Folder }],
            RecentDocs = documents,
            ModifiedDocs = documents,
            DocumentActivityByDay = activity,
            UserDocumentActivity = users
        });

        Assert.Empty(compact.AllDocs);
        Assert.Empty(compact.AllFolders);
        Assert.Equal(100, compact.RecentDocs.Count);
        Assert.Equal(100, compact.ModifiedDocs.Count);
        Assert.Equal(1_250, compact.TotalDocuments);
        Assert.Same(activity, compact.DocumentActivityByDay);
        Assert.Same(users, compact.UserDocumentActivity);
    }
}
