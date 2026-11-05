using LaserficheReports.Web.Permissions;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class PostSearchAuthorizationFilterTests
{
    private sealed class Gateway : IFullHierarchyAuthorizationService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> allowed = new();
        public int[] AuthorizedEntryIds => allowed.Keys.ToArray();
        public Task<bool> CanReadAsync(int id, CancellationToken ct)
        {
            if (id == 2) return Task.FromResult(false);
            if (id == 3) throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable));
            if (id == 4) throw new PermissionVerificationException(new(PermissionStatus.AuthenticationRequired));
            allowed.TryAdd(id, 0);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task MixedDecisionsKeepAllowedRowsAndMarkAllReportSectionsPartial()
    {
        var filter = new PostSearchAuthorizationFilter(new Gateway());
        var operations = new System.Collections.Concurrent.ConcurrentBag<int>();
        var allowed = await filter.FilterAsync([1, 2, 3, 1], (id, _) =>
        { operations.Add(id); return Task.CompletedTask; }, default);
        Assert.Equal(new[] { 1 }, allowed.Order());
        Assert.Equal(new[] { 1 }, operations.Order());
        var scope = new AnswerScope("repository", "repo", 1, 0, true, "", []);
        var result = filter.PrepareReport(new ChatResult("report", [], scope)
        { Reports = [new("section", "table", [], scope, [], null)] });
        Assert.StartsWith(PostSearchAuthorizationFilter.PartialNotice, result.Answer);
        Assert.False(result.Scope!.Exhaustive);
        Assert.False(result.Reports[0].Scope!.Exhaustive);
        Assert.StartsWith(PostSearchAuthorizationFilter.PartialNotice, result.Reports[0].Answer);
    }

    [Fact]
    public async Task CompleteAuthorizedReportRemainsUnchanged()
    {
        var filter = new PostSearchAuthorizationFilter(new Gateway());
        await filter.FilterAsync([1, 2], (_, _) => Task.CompletedTask, default);
        var result = new ChatResult("report", []);
        Assert.Same(result, filter.PrepareReport(result));
    }

    [Fact]
    public async Task CompleteBridgeFailureCannotBecomeSuccessfulEmptyReport()
    {
        var filter = new PostSearchAuthorizationFilter(new Gateway());
        Assert.Empty(await filter.FilterAsync([3], (_, _) => Task.CompletedTask, default));
        Assert.Throws<PermissionVerificationException>(() => filter.PrepareReport(new("empty", [])));
    }

    [Fact]
    public async Task AuthenticationFailureAndRequestCancellationStillAbort()
    {
        var filter = new PostSearchAuthorizationFilter(new Gateway());
        await Assert.ThrowsAsync<PermissionVerificationException>(() =>
            filter.FilterAsync([4], (_, _) => Task.CompletedTask, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            filter.FilterAsync([1], (_, _) => Task.CompletedTask, cancellation.Token));
    }

    [Fact]
    public async Task RequestedDataDenialExcludesOnlyItsCandidate()
    {
        var filter = new PostSearchAuthorizationFilter(new Gateway());
        var allowed = await filter.FilterAsync([1, 5], (id, _) => id == 5
            ? throw new LaserficheReports.Domain.Exceptions.LaserficheException("forbidden", 403)
            : Task.CompletedTask, default);
        Assert.Equal(new[] { 1 }, allowed.Order());
        Assert.False(filter.IsIncomplete);
    }
    [Fact]
    public async Task RequestedDataUnknownDoesNotExposeCandidateOrAffectAnotherRequest()
    {
        var filter = new PostSearchAuthorizationFilter(new Gateway());
        var allowed = await filter.FilterAsync([1, 5], (id, _) => id == 5
            ? throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable))
            : Task.CompletedTask, default);
        Assert.Equal(new[] { 1 }, allowed.Order());
        Assert.True(filter.IsIncomplete);
        Assert.False(new PostSearchAuthorizationFilter(new Gateway()).IsIncomplete);
    }
}
