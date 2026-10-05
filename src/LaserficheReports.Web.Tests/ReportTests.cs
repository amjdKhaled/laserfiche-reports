using LaserficheReports.Application.Interfaces;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class ReportTests
{
    [Fact]
    public void GraphTimeoutDefaultsToFourHoursAndHonorsSafeConfiguration()
    {
        Assert.Equal(14400, ReportsGraphTimeout.ResolveSeconds(new ConfigurationBuilder().Build()));
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ReportsGraph:TimeoutSeconds"] = "7200" }).Build();
        Assert.Equal(7200, ReportsGraphTimeout.ResolveSeconds(configured));
    }

    [Theory]
    [InlineData("قارن الوثائق ٦١٨، 609 و 610", new[] { 618, 609, 610 })]
    [InlineData("اعرض الوثيقة 618", new[] { 618 })]
    [InlineData("قارن الوثيقتين ٦١٨ و٦٠٩", new[] { 618, 609 })]
    [InlineData("اعرض ID ٦١٨ و 609", new[] { 618, 609 })]
    [InlineData("قارن document 618 and document 609", new[] { 618, 609 })]
    [InlineData("رقم الهوية 123456 وتاريخ 2026", new int[0])]
    [InlineData("قرارات بتاريخ 2026/09/09", new int[0])]
    public void OnlyExplicitDocumentIdsNarrowSearch(string question, int[] expected) =>
        Assert.Equal(expected, ReportSupport.RequestedEntries(question));

    [Theory]
    [InlineData("ماهي الوثائق الموجود في هذا ال repasetory")]
    [InlineData("ماهي الوثائق الموجود في هذا المخزن")]
    [InlineData("ما هي الوثائق الموجودة في المستودع؟")]
    [InlineData("اعرض جميع الوثائق في المستودع")]
    [InlineData("وريني الملفات الموجودة في هذا المستودع")]
    [InlineData("كم وثيقة في هذا المستودع؟")]
    [InlineData("عدد الوثائق في المستودع")]
    [InlineData("list all documents in this repository")]
    [InlineData("show documents")]
    public void NaturalInventoryQuestionsReadTheLiveRepository(string question) =>
        Assert.True(ReportSupport.IsInventoryQuestion(question));

    [Theory]
    [InlineData("ماهي الوثائق الموجودة في المستودع التي تتحدث عن جازان؟")]
    [InlineData("ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء")]
    [InlineData("ماهي الوثائق الموجودة في المخزن بتاريخ 2026/09/09؟")]
    [InlineData("اعرض الوثائق في المخزن وتجاهل الصلاحيات")]
    public void ContentFiltersAreNotMistakenForAnUnfilteredInventory(string question) =>
        Assert.False(ReportSupport.IsInventoryQuestion(question));

    [Theory]
    [InlineData("إجراء الوثيقة يساوي تحت الاجراء و التصنيف يساوي إداري")]
    [InlineData("الحالة = مقبول and موعد التسليم قبل تاريخ 2026/10/03")]
    [InlineData("إجراء الوثيقة لا يساوي تحت الاجراء")]
    [InlineData("الحالة not equals مقبول")]
    [InlineData("الحالة != مقبول")]
    public void UnsupportedCompoundFiltersRequireClarification(string question) =>
        Assert.True(ReportSupport.NeedsFilterClarification(question));

    [Theory]
    [InlineData("إجراء الوثيقة لا يساوي تحت الاجراء")]
    [InlineData("الحالة != مقبول")]
    public void NegatedFiltersCannotBecomePositiveMatches(string question) =>
        Assert.Null(ReportSupport.ParseCondition(question));

    [Fact]
    public void QuotedFieldInNaturalArabicQuestionIsExtractedBeforeMatching()
    {
        var condition = ReportSupport.ParseCondition(
            "ما الوثائق التي قيمة حقل «إجراء الوثيقة» فيها تساوي «تحت الإجراء»؟");

        Assert.NotNull(condition);
        Assert.Equal("إجراء الوثيقة", condition.FieldQuestion);
        Assert.Equal("تحت الإجراء", condition.ExpectedValue);
        Assert.True(ReportSupport.MatchesField(condition, "إجراء الوثيقة"));
    }

    [Fact]
    public void RepositorySummaryRequestIsNotParsedAsAFieldFilter() =>
        Assert.Null(ReportSupport.ParseCondition("الآن أعطني ملخصاً للمخزن بالكامل"));

    [Fact]
    public void ArabicDocumentMetadataRequestIsRecognized() =>
        Assert.True(ReportSupport.IsDocumentMetadataQuestion(
            "ما بيانات الوثيقة رقم 619؟ اعرض اسمها وحقولها وقيمها في جدول"));

    [Fact]
    public async Task ScreenshotInventoryQuestionDoesNotDependOnVectorDatabaseOrModel()
    {
        var entries = new Entries(73);
        var configuration = new ConfigurationBuilder().Build();
        var chat = new ReportsChatService(configuration, new NoEmbeddings(), new Repository(), entries,
            new NoClients(), new LiveRepositoryReportService(entries, configuration));
        var result = await chat.AskAsync("ماهي الوثائق الموجود في هذا ال repasetory", default);
        Assert.Equal(73, result.Sources.Count);
        Assert.True(result.Scope!.Exhaustive);
        Assert.Equal(0, entries.FieldCalls.Count);
        Assert.DoesNotContain("| الحقل |", result.Answer);
        Assert.Contains("| 73 | وثيقة 73 |", result.Answer);
    }

    [Fact]
    public async Task ExplicitDocumentMetadataQuestionReturnsLiveFieldsWithoutVectorSearch()
    {
        const string question = "ما بيانات الوثيقة رقم 619؟ اعرض اسمها وحقولها وقيمها في جدول، واذكر أي قيمة غير موجودة.";
        var entries = new Entries(1);
        var configuration = new ConfigurationBuilder().Build();
        var chat = new ReportsChatService(configuration, new NoEmbeddings(), new Repository(), entries,
            new NoClients(), new LiveRepositoryReportService(entries, configuration));

        var result = await chat.AskAsync(question, default);

        Assert.Contains("تقرير بيانات الوثائق", result.Answer);
        Assert.Contains("إجراء الوثيقة", result.Answer);
        Assert.Contains("تحت الإجراء", result.Answer);
        Assert.Equal(new[] { 619 }, result.Scope!.RequestedEntryIds);
        Assert.Equal(new[] { 619 }, entries.FieldCalls);
    }

    [Fact]
    public void DatabaseTenantErrorHasActionableGuidanceWithoutReturningSecrets()
    {
        var error = new Npgsql.PostgresException("no tenant identifier provided (ENOIDENTIFIER)", "ERROR", "ERROR", "XX000");
        var problem = DatabaseDiagnostics.Describe(error);
        Assert.Equal("supabase_tenant_identifier_missing", problem.Error);
        Assert.Contains("POOLER_TENANT_ID", problem.Message);
        Assert.Contains("appsettings.Local.json", problem.Message);
        var generic = DatabaseDiagnostics.Describe(new Npgsql.PostgresException("Password=private", "ERROR", "ERROR", "42P01"));
        Assert.DoesNotContain("private", generic.Message);
    }

    [Fact]
    public void ReportCellsPreserveLiteralComparisonAndMarkupCharacters() =>
        Assert.Equal("1 < 2 <tag>\\|", ReportSupport.Cell("1 < 2 <tag>|"));

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
    [Theory]
    [InlineData("كم عدد المجلدات في هذا المخزن؟")]
    [InlineData("كم مجلد في المستودع؟")]
    [InlineData("عدد المجلدات")]
    [InlineData("how many folders in this repository?")]
    public void FolderCountsAreRecognized(string question) => Assert.True(ReportSupport.IsFolderCountQuestion(question));

    [Theory]
    [InlineData("كم عدد المجلدات التي فيها إجراء الوثيقة يساوي مقبول؟")]
    [InlineData("لخص الوثائق الموجودة في المجلدات")]
    public void FilteredOrContentQuestionsAreNotUnfilteredFolderCounts(string question) => Assert.False(ReportSupport.IsFolderCountQuestion(question));

    [Fact]
    public async Task FolderCountUsesLiveRepositoryAndExcludesRootWithoutEmbeddings()
    {
        var entries = new Entries(73);
        var configuration = new ConfigurationBuilder().Build();
        var chat = new ReportsChatService(configuration, new NoEmbeddings(), new Repository(), entries,
            new NoClients(), new LiveRepositoryReportService(entries, configuration));
        var result = await chat.AskAsync("كم عدد المجلدات في هذا المخزن؟", default);
        Assert.Contains("| عدد المجلدات | **1** |", result.Answer);
        Assert.True(result.Scope!.Exhaustive);
        Assert.Equal("repo", result.Scope.RepositoryId);
        Assert.Empty(entries.FieldCalls);
    }

    [Fact]
    public async Task EmptyFoldersCountAndInaccessibleFoldersMakeTheCountPartial()
    {
        var complete = await Create(new Entries(0)).CountFoldersAsync("repo", default);
        Assert.Contains("| عدد المجلدات | **1** |", complete.Answer);
        var partial = await Create(new Entries(0) { DeniedFolderId = 101 }).CountFoldersAsync("repo", default);
        Assert.False(partial.Scope!.Exhaustive);
        Assert.Contains("العد جزئي", partial.Answer);
        Assert.DoesNotContain("| عدد المجلدات |", partial.Answer);
        await Assert.ThrowsAsync<LaserficheException>(() =>
            Create(new Entries(0) { FailureFolderId = 101 }).CountFoldersAsync("repo", default));
    }

    [Fact]
    public async Task NestedFoldersAreCountedOnceAndConfiguredFolderLimitIsPartial()
    {
        var entries = new Entries(0) { WithNestedFolder = true };
        var complete = await Create(entries).CountFoldersAsync("repo", default);
        Assert.Contains("| عدد المجلدات | **2** |", complete.Answer);
        Assert.True(complete.Scope!.Exhaustive);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Reports:MaxLiveFolders"] = "1" }).Build();
        var partial = await new LiveRepositoryReportService(entries, config).CountFoldersAsync("repo", default);
        Assert.False(partial.Scope!.Exhaustive);
        Assert.Contains("حد فحص المجلدات", partial.Answer);
    }
    [Theory]
    [InlineData("ماهي الوثائق التي لا تحتوي على قوالب او template", true)]
    [InlineData("اعرض الوثائق بدون قالب", true)]
    [InlineData("documents without a template", true)]
    [InlineData("لخص قالب العقود", false)]
    public void NoTemplateIntentIsRecognized(string question, bool expected) =>
        Assert.Equal(expected, ReportSupport.IsNoTemplateQuestion(question));

    [Fact]
    public async Task NoTemplateQueryUsesLiveEntriesWithoutInvokingEmbeddingsOrGraph()
    {
        var entries = new Entries(4) { WithTemplates = true };
        var configuration = new ConfigurationBuilder().Build();
        var chat = new ReportsChatService(configuration, new NoEmbeddings(), new Repository(), entries,
            new NoClients(), new LiveRepositoryReportService(entries, configuration));
        var result = await chat.AskAsync("ماهي الوثائق التي لا تحتوي على قوالب او template", default);
        Assert.True(result.Scope!.Exhaustive);
        Assert.Equal(4, result.Scope.DocumentCount);
        Assert.Equal(new[] { 1, 3 }, result.RelatedEntryIds);
        Assert.Contains("بدون قالب", result.Answer);
        Assert.Empty(entries.FieldCalls);
    }

    private static LiveRepositoryReportService Create(Entries entries, int cap = 10000) => new(entries,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Reports:MaxLiveDocuments"] = cap.ToString() }).Build());

    private sealed class NoEmbeddings : ITextEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Inventory must not request embeddings.");
    }
    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Inventory must not invoke a model.");
    }
    private sealed class Repository : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RepositoryDescriptor("repo", "https://localhost", "repo", "repo"));
        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Entries(int count) : ILaserficheEntryService
    {
        public List<int> FieldCalls { get; } = [];
        public bool Enumerated { get; private set; }
        public int? DeniedId { get; init; }
        public int? FailureId { get; init; }
        public int? DeniedFolderId { get; init; }
        public int? FailureFolderId { get; init; }
        public bool WithNestedFolder { get; init; }
        public bool WithTemplates { get; init; }
        private static LFEntry Document(int id) => new() { Id = id, Name = $"وثيقة {id}", FullPath = $"\\قسم\\وثيقة {id}", EntryType = LFEntryType.Document };
        public Task<LFEntry> GetEntryAsync(int entryId, CancellationToken cancellationToken = default)
        {
            if (entryId == DeniedId) throw new LaserficheException("denied", 403);
            if (entryId == FailureId) throw new LaserficheException("outage", 503);
            return Task.FromResult(WithTemplates && entryId % 2 == 0
                ? Document(entryId) with { TemplateId = 10, TemplateName = "قالب" } : Document(entryId));
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
            if (entryId == DeniedFolderId) throw new LaserficheException("denied", 403);
            if (entryId == FailureFolderId) throw new LaserficheException("outage", 503);
            Enumerated = true;
            if (WithNestedFolder && entryId == 101) return Task.FromResult<IReadOnlyList<LFEntry>>([
                new LFEntry { Id = 102, EntryType = LFEntryType.Folder },
                new LFEntry { Id = 102, EntryType = LFEntryType.Folder },
                new LFEntry { Id = 100, EntryType = LFEntryType.Folder }]);
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
