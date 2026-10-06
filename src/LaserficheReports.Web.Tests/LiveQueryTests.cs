using LaserficheReports.Web;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class LiveQueryTests
{
    [Theory]
    [InlineData("templates", "templates")]
    [InlineData("folder", "folder")]
    [InlineData("search", "search")]
    [InlineData("report", "search")]
    public void PagingPreservesToolAndFilters(string intent, string expectedIntent)
    {
        var query = new RepositoryQuery { Intent = intent, FolderId = 10, Field = "الحالة", Value = "مكتمل" };
        var next = query.ForPage(2);
        Assert.Equal(expectedIntent, next.Intent);
        Assert.Equal(2, next.Page);
        Assert.Equal(query.FolderId, next.FolderId);
        Assert.Equal(query.Field, next.Field);
        Assert.Equal(query.Value, next.Value);
        Assert.Equal(1, query.Page);
    }
    [Fact]
    public void InvalidGroupingIsAValidationError()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryQuery { GroupBy = null! }.Validate());
        Assert.Throws<ArgumentException>(() => new RepositoryQuery { GroupBy = [null!] }.Validate());
        Assert.Throws<ArgumentException>(() => new RepositoryQuery { GroupBy = [" "] }.Validate());
    }
    [Theory]
    [InlineData("كم عدد الوثائق الموجودة في المستودع؟", "count", "documents")]
    [InlineData("كم عدد المجلدات؟", "count", "folders")]
    [InlineData("ما القوالب الموجودة؟", "templates", "documents")]
    [InlineData("اعرض آخر 10 وثائق معدلة.", "search", "documents")]
    public void RoutesCommonQuestions(string question, string intent, string type)
    {
        var plan = QuestionRouter.TryRoute(question, new(2026, 10, 5));
        Assert.NotNull(plan); Assert.Equal(intent, plan.Intent); Assert.Equal(type, plan.EntryType);
    }
    [Theory]
    [InlineData("ما الوثائق التي إجراء الوثيقة فيها تحت الإجراء؟", "تحت الإجراء")]
    [InlineData("ما الوثائق التي إجراء الوثيقة فيها مكتمل؟", "مكتمل")]
    public void NaturalFieldQuestionPreservesArabic(string question, string expected)
    {
        var plan = QuestionRouter.TryRoute(question, new(2026, 10, 5));
        Assert.NotNull(plan); Assert.Equal("إجراء الوثيقة", plan.Field); Assert.Equal(expected, plan.Value);
    }
    [Fact]
    public void TodayIsLocalAndDatesBuildAsConfigured()
    {
        var today = new DateOnly(2026, 10, 5);
        var plan = QuestionRouter.TryRoute("اعرض الوثائق المنشأة اليوم.", today)!;
        Assert.Equal(today, plan.StartDate); Assert.Equal(today, plan.EndDate);
        Assert.Contains("Created>=\"10/05/2026\"", plan.Expression("MM/dd/yyyy"));
    }
    [Theory]
    [InlineData("a\"} | {LF:Name=\"*")]
    [InlineData("*")]
    [InlineData("foo\nbar")]
    public void RejectsQueryInjection(string value) => Assert.Throws<ArgumentException>(() => new RepositoryQuery { Name = value }.Validate());
    [Fact]
    public void RejectsInvalidPagingSortAndDates()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryQuery { PageSize = 101 }.Validate());
        Assert.Throws<ArgumentException>(() => new RepositoryQuery { Sort = "arbitrary" }.Validate());
        Assert.Throws<ArgumentException>(() => new RepositoryQuery { EntryId = -1 }.Validate());
        Assert.Throws<ArgumentException>(() => new RepositoryQuery { StartDate = new(2026, 10, 6), EndDate = new(2026, 10, 5) }.Validate());
    }
    [Fact]
    public void RepositoryGenerationCancelsOldRequestsWithoutMixingUsers()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var registry = new SessionRequestRegistry(cache);
        var a = registry.Get("user-a", "repo-a-generation");
        var b = registry.Get("user-b", "repo-a-generation");
        registry.Cancel("user-a");
        Assert.True(a.IsCancellationRequested); Assert.False(b.IsCancellationRequested);
        Assert.False(registry.Get("user-a", "repo-b-generation").IsCancellationRequested);
    }
    [Fact]
    public async Task CountUsesSearchTotalWithoutScanningEntries()
    {
        var search = new SearchStub((page, size) => new() { TotalCount = 123456, PageNumber = page, PageSize = size });
        var tool = new LaserficheToolExecutor(search, null!, null!, null!, null!, new ConfigurationBuilder().Build(), NullLogger<LaserficheToolExecutor>.Instance);
        var result = await tool.ExecuteAsync(new() { Intent = "count" }, CancellationToken.None);
        Assert.Equal(123456, result.TotalCount); Assert.Empty(result.Items); Assert.Equal(1, search.Calls);
    }
    [Fact]
    public async Task CompleteReportAggregatesAllPagesAndKeepsOnlySmallPreview()
    {
        var search = new SearchStub((page, size) => new() { Items = Enumerable.Range((page - 1) * size + 1, page == 1 ? 100 : 1)
            .Select(id => new LFSearchResult { EntryId = id, Name = "وثيقة", FieldValues = new Dictionary<string,string?> { ["الإدارة"] = id % 2 == 0 ? "أ" : "ب" } }).ToArray(),
            TotalCount = 101, PageNumber = page, PageSize = size });
        var tool = new LaserficheToolExecutor(search, null!, null!, null!, null!, new ConfigurationBuilder().Build(), NullLogger<LaserficheToolExecutor>.Instance);
        var result = await tool.ExecuteAsync(new() { Intent = "report", GroupBy = ["الإدارة"], PageSize = 20 }, CancellationToken.None);
        Assert.Equal(101, result.TotalCount); Assert.Equal(20, result.Items.Count); Assert.Equal(101, result.Groups!.Values.Sum()); Assert.Equal(2, search.Calls);
    }
    [Fact]
    public async Task MissingFieldProjectionCannotBecomeFalseStatistics()
    {
        var search = new SearchStub((page, size) => new() { Items = [new() { EntryId = 1 }], TotalCount = 1 });
        var tool = new LaserficheToolExecutor(search, null!, null!, null!, null!, new ConfigurationBuilder().Build(), NullLogger<LaserficheToolExecutor>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(new() { Intent = "report", GroupBy = ["الإدارة"] }, CancellationToken.None));
    }
    private sealed class SearchStub(Func<int,int,PagedResult<LFSearchResult>> response) : ILaserficheSearchService
    {
        public int Calls;
        public Task<PagedResult<LFSearchResult>> AdvancedSearchAsync(string query, int page, int size, CancellationToken ct = default) { Calls++; return Task.FromResult(response(page,size)); }
        public Task<PagedResult<LFSearchResult>> SimpleSearchAsync(string query, int page, int size, CancellationToken ct = default) => AdvancedSearchAsync(query,page,size,ct);
        public Task<PagedResult<LFSearchResult>> SearchByTemplateAsync(string name, int page, int size, CancellationToken ct = default) => AdvancedSearchAsync(name,page,size,ct);
        public Task<PagedResult<LFSearchResult>> SearchByFieldAsync(string field, string value, int page, int size, CancellationToken ct = default) => AdvancedSearchAsync(field,page,size,ct);
    }
}
