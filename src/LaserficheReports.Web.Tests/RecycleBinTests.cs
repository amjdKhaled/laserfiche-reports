using System.Text.Json;
using Xunit;
namespace LaserficheReports.Web.Tests;
public class RecycleBinTests
{
    private static RecycleBinSnapshot Snapshot() => new("recycle-bin-v1", "Repo", "alice", "current_user_visible", true,
        [new(61, "وثيقة | خاصة", "document", "alice", DateTimeOffset.Parse("2026-10-19T00:00:00Z"), 2),
         new(62, "مجلد", "folder", "bob", DateTimeOffset.Parse("2026-10-18T00:00:00Z"), null),
         new(63, "وثيقة ثانية", "document", "bob", DateTimeOffset.Parse("2026-10-19T01:00:00Z"), 1)]);
    [Fact] public void SnapshotRequiresCompleteMatchingIdentityAndValidRows()
    {
        string Json(RecycleBinSnapshot snapshot) => JsonSerializer.Serialize(snapshot);
        Assert.Equal(3, RecycleBinSource.Parse(Json(Snapshot()), "Repo", "alice").Entries.Length);
        foreach (var bad in new[] { Snapshot() with { Complete = false }, Snapshot() with { RepositoryId = "Other" },
            Snapshot() with { Username = "other" }, Snapshot() with { Scope = "all_users" },
            Snapshot() with { Entries = [Snapshot().Entries[0], Snapshot().Entries[0]] },
            Snapshot() with { Entries = [Snapshot().Entries[0] with { EntryType = "deletedPage" }] } })
            Assert.Throws<RecycleBinException>(() => RecycleBinSource.Parse(Json(bad), "Repo", "alice"));
    }
    [Fact] public void ListingCountsOnlyDocumentsPagesByDeletionAndHasNoLiveLinks()
    {
        var first = RecycleBinReport.Build(Snapshot(), "Repo", new("recycle_bin", Limit: 1));
        Assert.Contains("وثيقة ثانية", first.Answer);
        Assert.Throws<RecycleBinException>(() => RecycleBinReport.Build(Snapshot(), "Other", new("recycle_bin")));
        Assert.DoesNotContain("وثيقة \\| خاصة", first.Answer);
        Assert.Contains("| 2 | 1 | 2 |", first.Answer);
        Assert.Equal(2, first.Scope!.DocumentCount);
        Assert.Empty(first.RelatedEntryIds); Assert.Empty(first.Sources);
        var second = RecycleBinReport.Build(Snapshot(), "Repo", new("recycle_bin", Limit: 1, Page: 2));
        Assert.Contains("2026-10-19 03:00:00", second.Answer);
        Assert.Contains("وثيقة \\| خاصة", second.Answer);
        var count = RecycleBinReport.Build(Snapshot(), "Repo", new("recycle_bin", CountOnly: true));
        Assert.Contains("| 2 |", count.Answer);
        Assert.Contains("لا تشمل المحذوف نهائيًا", count.Answer);
        Assert.Contains("| 0 |", RecycleBinReport.Build(Snapshot() with { Entries = [] }, "Repo", new("recycle_bin", CountOnly: true)).Answer);
    }
    [Fact] public void UnsupportedConditionsCannotBeSilentlyIgnored()
    {
        foreach (var plan in new[] { new QueryPlan("recycle_bin", Content: true), new("recycle_bin", Name: "ignored"),
            new("recycle_bin", Audit: new()), new("recycle_bin", From: "2026-01-01"), new("recycle_bin", ResultType: "statistics") })
            Assert.Throws<ArgumentException>(() => plan.ValidateIntent());
    }
    [Fact] public async Task SettingsMergePreservesApiAuditOtherRepositoriesAndRejectsCorruption()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "runtime.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try {
            await File.WriteAllTextAsync(path, "{\"Laserfiche\":{\"ServerUrl\":\"https://existing\"},\"AuditTrail\":{\"Enabled\":true},\"RecycleBin\":{\"Repositories\":{\"Other\":{\"Enabled\":true}}}}");
            await RecycleBinConfiguration.SaveFileAsync(path, "Repo", new() { Enabled = true, ServerName = "LFServer", RepositoryName = "NativeRepo" }, default);
            using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("https://existing", data.RootElement.GetProperty("Laserfiche").GetProperty("ServerUrl").GetString());
            Assert.True(data.RootElement.GetProperty("AuditTrail").GetProperty("Enabled").GetBoolean());
            Assert.True(data.RootElement.GetProperty("RecycleBin").GetProperty("Repositories").TryGetProperty("Other", out _));
            await File.WriteAllTextAsync(path, "corrupt");
            await Assert.ThrowsAsync<ArgumentException>(() => RecycleBinConfiguration.SaveFileAsync(path, "Repo", new(), default));
            Assert.Equal("corrupt", await File.ReadAllTextAsync(path));
        } finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
