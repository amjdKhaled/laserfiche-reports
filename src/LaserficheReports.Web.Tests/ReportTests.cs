using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class ReportTests
{
    [Theory]
    [InlineData("قارن الوثائق ٦١٨، 609 و 610", new[] { 618, 609, 610 })]
    [InlineData("اعرض الوثيقة 618", new[] { 618 })]
    [InlineData("قرارات بتاريخ 2026/09/09", new int[0])]
    public void OnlyExplicitDocumentIdsNarrowSearch(string question, int[] expected) =>
        Assert.Equal(expected, ReportSupport.RequestedEntries(question));

    [Fact]
    public void ArabicFieldsNormalizeWithoutSubstringValueMatches()
    {
        var condition = ReportSupport.ParseCondition("ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء؟")!;
        Assert.True(ReportSupport.MatchesField(condition, "إ جراء الوثيقة"));
        Assert.True(ReportSupport.MatchesValue("تحت الإجراء", condition.ExpectedValue));
        Assert.False(ReportSupport.MatchesValue("ليس تحت الإجراء", condition.ExpectedValue));
        Assert.True(ReportSupport.MatchesValue("رفض, تحت الاجراء", condition.ExpectedValue, true));
        Assert.False(ReportSupport.MatchesValue("رفض, تحت الاجراء", condition.ExpectedValue));
    }

    [Fact]
    public void EvidenceSelectionSharesContextAcrossDocumentsAndDeduplicates()
    {
        var first = new Evidence(1, "الأول", "", 1, 1, "أ", "ocr");
        var second = first with { PageNumber = 2, Text = "ب" };
        var third = first with { EntryId = 2, DocumentName = "الثاني" };
        Assert.Equal(new[] { first, third, second }, ReportSupport.SelectEvidence([first, first, second, third], 3));
    }

    [Fact]
    public async Task FieldReportsInspectAll73DocumentsWithoutTopKOrModel()
    {
        var service = new Entries(73);
        var report = await Create(service).CreateAsync("repo", Condition(), [], default);
        Assert.Equal(73, report.Scope!.DocumentCount);
        Assert.True(report.Scope.Exhaustive);
        Assert.Equal(37, report.Sources.Count);
        Assert.Contains(report.Sources, e => e.EntryId == 73);
        Assert.Equal(73, service.FieldCalls.Count);
        Assert.Contains("| رقم الوثيقة |", report.Answer);
        Assert.Contains("عدد الوثائق المطابقة: **37**", report.Answer);
    }

    [Fact]
    public async Task ExplicitScopeDoesNotEnumerateOtherDocuments()
    {
        var service = new Entries(73);
        var report = await Create(service).CreateAsync("repo", Condition(), [73], default);
        Assert.False(service.Enumerated);
        Assert.Equal(new[] { 73 }, service.FieldCalls);
        Assert.Single(report.Sources);
        Assert.Equal("selected-documents", report.Scope!.Mode);
    }

    [Fact]
    public async Task AccessDenialAndConfiguredCapCannotClaimCompleteReport()
    {
        var denied = new Entries(3) { DeniedId = 3 };
        var partial = await Create(denied).CreateAsync("repo", Condition(), [], default);
        Assert.False(partial.Scope!.Exhaustive);
        Assert.DoesNotContain(partial.Sources, e => e.EntryId == 3);
        var capped = await Create(new Entries(3), 2).CreateAsync("repo", null, [], default);
        Assert.False(capped.Scope!.Exhaustive);
        Assert.Equal(2, capped.Sources.Count);
    }

    [Fact]
    public async Task OutageFailsInsteadOfReportingFalseCount()
    {
        await Assert.ThrowsAsync<LaserficheException>(() =>
            Create(new Entries(3) { FailureId = 2 }).CreateAsync("repo", Condition(), [], default));
    }

    [Fact]
    public async Task MissingFieldDoesNotAssertZeroMatches()
    {
        var report = await Create(new Entries(2)).CreateAsync("repo", new FieldCondition("حقل غير معروف", "قيمة"), [], default);
        Assert.Contains("لم أتعرف على اسم الحقل", report.Answer);
        Assert.DoesNotContain("عدد الوثائق المطابقة: **0**", report.Answer);
    }

    private static FieldCondition Condition() => ReportSupport.ParseCondition("إجراء الوثيقة يساوي تحت الاجراء")!;
    private static LiveRepositoryReportService Create(Entries entries, int cap = 10000) => new(entries,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Reports:MaxLiveDocuments"] = cap.ToString() }).Build());

    private sealed class Entries(int count) : ILaserficheEntryService
    {
        public List<int> FieldCalls { get; } = [];
        public bool Enumerated { get; private set; }
        public int? DeniedId { get; init; }
        public int? FailureId { get; init; }
        private static LFEntry Document(int id) => new() { Id = id, Name = $"وثيقة {id}", FullPath = $"\\قسم\\وثيقة {id}", EntryType = LFEntryType.Document };
        public Task<LFEntry> GetEntryAsync(int entryId, CancellationToken cancellationToken = default)
        {
            if (entryId == DeniedId) throw new LaserficheException("denied", 403);
            if (entryId == FailureId) throw new LaserficheException("outage", 503);
            return Task.FromResult(Document(entryId));
        }
        public Task<IReadOnlyList<LFFieldValue>> GetEntryFieldsAsync(int entryId, CancellationToken cancellationToken = default)
        {
            FieldCalls.Add(entryId);
            return Task.FromResult<IReadOnlyList<LFFieldValue>>([
                new() { FieldName = "الوثيقة", Value = "حقل أقصر" },
                new() { FieldName = "إجراء الوثيقة", Value = entryId % 2 == 1 ? "تحت الإجراء" : "تم الرفض" }]);
        }
        public Task<int> GetRootEntryIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(100);
        public Task<IReadOnlyList<LFEntry>> GetAllFolderChildrenAsync(int entryId, CancellationToken cancellationToken = default)
        {
            Enumerated = true;
            return Task.FromResult<IReadOnlyList<LFEntry>>(entryId == 100
                ? [new LFEntry { Id = 101, EntryType = LFEntryType.Folder }]
                : Enumerable.Range(1, count).Select(Document).ToArray());
        }
        public Task<string> GetEntryPathAsync(int entryId, CancellationToken cancellationToken = default) => Task.FromResult(Document(entryId).FullPath);
        public Task<LFTemplate?> GetEntryTemplateAsync(int entryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<LFEntry>> GetEntryChildrenAsync(int entryId, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LFEntry>> GetFolderTreeAsync(int rootEntryId, int depth, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
