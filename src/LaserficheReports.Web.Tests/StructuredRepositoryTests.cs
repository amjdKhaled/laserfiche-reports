using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Web;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class StructuredRepositoryTests
{
    [Fact]
    public void DocumentIntentRejectsAggregationAndMissingSelectionBeforeLiveExecution()
    {
        Assert.Throws<ArgumentException>(() => new QueryPlan("group", ResultType: "documents").ValidateIntent());
        Assert.Throws<ArgumentException>(() => new QueryPlan("search", ResultType: "documents", RequiresFilter: true).ValidateIntent());
        new QueryPlan("search", ResultType: "documents", RequiresFilter: true, Filters: new("القسم", "equals", "أ")).ValidateIntent();
        new QueryPlan("group", ResultType: "statistics").ValidateIntent();
    }
    private static readonly LFFieldDefinition[] Schema = [
        new() { Name = "موعد الإنجاز", FieldType = "Date" },
        new() { Name = "القسم", FieldType = "String" },
        new() { Name = "الحالة", FieldType = "String" },
        new() { Name = "التكلفة", FieldType = "Number" }];
    private static readonly DateOnly Today = new(2026, 10, 7);

    [Fact]
    public void NestedConditionsPreserveOrNegationAndRelativeDeadline()
    {
        var filter = new RepositoryFilter(Logic: "and", Conditions: [
            new("موعد الإنجاز", "less_than", Relative: new()),
            new(Logic: "or", Conditions: [new("القسم", "equals", "أ"), new("الحالة", "not_equals", "مغلق")])]);
        Assert.Equal("({[]:[موعد الإنجاز]<\"2026-10-07\"} & ({[]:[القسم]=\"أ\"} | {[]:[الحالة]<>\"مغلق\"}))",
            StructuredRepositoryQuery.Compile(filter, Schema, Today));
    }

    [Theory]
    [InlineData("day", -1, "start", "2026-10-06")]
    [InlineData("day", -1, "end", "2026-10-07")]
    [InlineData("week", -1, "start", "2026-09-27")]
    [InlineData("week", -1, "end", "2026-10-04")]
    [InlineData("month", 0, "start", "2026-10-01")]
    [InlineData("month", 0, "end", "2026-11-01")]
    [InlineData("month", -3, "rolling", "2026-07-07")]
    [InlineData("year", 0, "start", "2026-01-01")]
    [InlineData("year", 0, "end", "2027-01-01")]
    public void RelativeDatesUseBackendCalendar(string unit, int offset, string boundary, string expected) =>
        Assert.Equal(expected, RepositoryDates.Resolve(new(Unit: unit, Offset: offset, Boundary: boundary), Today).ToString("yyyy-MM-dd"));

    [Theory]
    [InlineData("equals", "{[]:[القسم]=\"مالية\"}")]
    [InlineData("not_equals", "{[]:[القسم]<>\"مالية\"}")]
    [InlineData("contains", "{[]:[القسم]=\"*مالية*\"}")]
    [InlineData("starts_with", "{[]:[القسم]=\"مالية*\"}")]
    public void TextOperatorsGenerateOnlyBackendOwnedSyntax(string op, string expected) =>
        Assert.Equal(expected, StructuredRepositoryQuery.Compile(new("القسم", op, "مالية"), Schema, Today));

    [Theory]
    [InlineData("is_empty", "=")]
    [InlineData("is_not_empty", "<>")]
    public void BlankFieldsAreNotNumericZero(string op, string symbol) =>
        Assert.Equal("{[]:[التكلفة]" + symbol + "\"\"}", StructuredRepositoryQuery.Compile(new("التكلفة", op), Schema, Today));

    [Fact]
    public void NumericAndPageRangeUseActualNumericOperators()
    {
        Assert.Equal("({[]:[التكلفة]>=\"-5.5\"} & {[]:[التكلفة]<=\"100\"})", StructuredRepositoryQuery.Compile(new("التكلفة", "between", "-5.5", "100"), Schema, Today));
        Assert.Equal("{LF:PageCount>10}", StructuredRepositoryQuery.Compile(new("pageCount", "greater_than", "10"), Schema, Today));
    }

    [Theory]
    [InlineData("غير موجود", "equals", "أ")]
    [InlineData("القسم", "less_than", "أ")]
    [InlineData("التكلفة", "equals", "drop table")]
    [InlineData("موعد الإنجاز", "date_before", "2026-02-30")]
    [InlineData("القسم", "equals", "أ\"} | {LF:ID=99}")]
    [InlineData("القسم", "equals", "*")]
    [InlineData("pageCount", "equals", "1.5")]
    public void InvalidFieldsTypesValuesAndQueryInjectionNeverExecute(string field, string op, string value) =>
        Assert.Throws<ArgumentException>(() => StructuredRepositoryQuery.Compile(new(field, op, value), Schema, Today));

    [Fact]
    public void InvertedRangeAndMixedRelativeLiteralAreRejected()
    {
        Assert.Throws<ArgumentException>(() => StructuredRepositoryQuery.Compile(new("التكلفة", "between", "100", "2"), Schema, Today));
        Assert.Throws<ArgumentException>(() => StructuredRepositoryQuery.Compile(new("موعد الإنجاز", "less_than", "2026-10-07", Relative: new()), Schema, Today));
        Assert.Throws<ArgumentException>(() => StructuredRepositoryQuery.Compile(new("التكلفة", "less_than", Relative: new()), Schema, Today));
    }

    private static LFSearchResult Row(int id, string department, string state, string? cost, string date = "2026-10-01") => new()
    {
        EntryId = id, CreationTime = DateTimeOffset.Parse(date + "T00:00:00+03:00"),
        Fields = [new("القسم", [department], false), new("الحالة", [state], false), new("التكلفة", cost is null ? [] : [cost], false)]
    };

    [Fact]
    public void AggregationComputesMultipleDimensionsAndMetricsBeforeTopN()
    {
        var data = new PagedResult<LFSearchResult> { Items = [Row(1, "أ", "مفتوح", "10"), Row(2, "أ", "مفتوح", "20"), Row(3, "ب", "مغلق", null)], TotalCount = 3 };
        var plan = new QueryPlan("group", Limit: 1, GroupFields: [new("القسم"), new("الحالة")], Metrics: [new("count"), new("sum", "التكلفة"), new("average", "التكلفة")]);
        var report = RepositoryAggregation.Render("repo", plan, data, Schema, []);
        Assert.Contains("| أ | مفتوح | 2 | 30 | 15 |", report.Answer);
        Assert.DoesNotContain("| ب | مغلق |", report.Answer);
        Assert.Equal(3, report.Scope!.DocumentCount);
    }

    [Fact]
    public void MissingAggregateValueStaysUnknownAndPartialPagesCannotAggregate()
    {
        var data = new PagedResult<LFSearchResult> { Items = [Row(1, "ب", "مغلق", null)], TotalCount = 1 };
        var plan = new QueryPlan("group", GroupFields: [new("القسم")], Metrics: [new("sum", "التكلفة")]);
        Assert.Contains("| ب | — |", RepositoryAggregation.Render("repo", plan, data, Schema, []).Answer);
        Assert.Throws<ArgumentException>(() => RepositoryAggregation.Render("repo", plan, data with { TotalCount = 50, HasMore = true }, Schema, []));
    }

    [Fact]
    public void MonthlyTrendAndDistinctCountsAreExact()
    {
        var data = new PagedResult<LFSearchResult> { Items = [Row(1, "أ", "مفتوح", "0"), Row(2, "أ", "مغلق", "0"), Row(3, "ب", "مغلق", "0", "2026-09-01")], TotalCount = 3 };
        var report = RepositoryAggregation.Render("repo", new QueryPlan("group", GroupFields: [new("created", "month")], Metrics: [new("count"), new("distinct_count", "القسم")]), data, Schema, []);
        Assert.Contains("| 2026-10 | 2 | 1 |", report.Answer);
        Assert.Contains("| 2026-09 | 1 | 1 |", report.Answer);
    }

    [Fact]
    public void PostAggregationConditionsAndRollupUseAllGroupsBeforePaging()
    {
        var data = new PagedResult<LFSearchResult> { Items = [Row(1, "أ", "مفتوح", "1"), Row(2, "أ", "مفتوح", "1"), Row(3, "ب", "مفتوح", "1", "2026-09-01")], TotalCount = 3 };
        var report = RepositoryAggregation.Render("repo", new QueryPlan("group", Limit: 1, GroupFields: [new("created", "month")], Metrics: [new("count")], Rollup: "average"), data, Schema, []);
        Assert.Contains("**1.5**", report.Answer);
        report = RepositoryAggregation.Render("repo", new QueryPlan("group", GroupFields: [new("القسم")], Metrics: [new("count")], Having: new(0, "greater_than", 1)), data, Schema, []);
        Assert.Contains("| أ | 2 |", report.Answer);
        Assert.DoesNotContain("| ب |", report.Answer);
    }

    [Fact]
    public void MetadataOrderingIsNumericAndPagesAfterGlobalSort()
    {
        var data = new PagedResult<LFSearchResult> { Items = [Row(1, "أ", "مفتوح", "100"), Row(2, "أ", "مفتوح", "2"), Row(3, "أ", "مفتوح", "30")], TotalCount = 3 };
        var result = RepositoryAggregation.SortPage(data, new QueryPlan("search", Limit: 1, Page: 2, SortField: "التكلفة"), Schema);
        Assert.Equal(3, Assert.Single(result.Items).EntryId);
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public void CompleteMetadataSortKeepsAllRowsBeyondInternalBatchSize()
    {
        var data = new PagedResult<LFSearchResult> { Items = [Row(1, "أ", "مفتوح", "100"), Row(2, "أ", "مفتوح", "2"), Row(3, "أ", "مفتوح", "30")], TotalCount = 3 };
        var result = RepositoryAggregation.SortPage(data, new QueryPlan("search", Limit: 1, SortField: "التكلفة", AllResults: true), Schema);
        Assert.Equal(new[] { 2, 3, 1 }, result.Items.Select(i => i.EntryId));
        Assert.False(result.HasNextPage);
    }
}
