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

    [Theory]
    [InlineData("كم عدد الوثائق في المستودع؟", "search", "LASERFICHE_QUERY")]
    [InlineData("ماهي الوثائق التي إجراء الوثيقة فيها تحت الإجراء؟", "search", "LASERFICHE_QUERY")]
    [InlineData("كم وثيقة تحت الإجراء؟", "search", "LASERFICHE_QUERY")]
    [InlineData("اعطيني الوثائق التي تحتوي على تحت الاجراء", "search", "LASERFICHE_QUERY")]
    [InlineData("اعطيني الوثائق التي تحتوي على حالة الوثيقة = تحت الاجراء", "search", "LASERFICHE_QUERY")]
    [InlineData("ما آخر 10 وثائق معدلة؟", "recent", "LASERFICHE_QUERY")]
    [InlineData("ما الوثائق المنشأة اليوم؟", "created", "LASERFICHE_QUERY")]
    [InlineData("ما القوالب الموجودة؟", "templates", "LASERFICHE_QUERY")]
    [InlineData("ما Metadata الوثيقة 618؟", "metadata", "LASERFICHE_QUERY")]
    [InlineData("ما محتوى الوثيقة 618؟", "content", "OCR_QUERY")]
    [InlineData("لخص محتوى الوثيقة 618.", "content", "OCR_QUERY")]
    [InlineData("لخص محتوى الوثائق التي إجراء الوثيقة فيها تحت الإجراء.", "search", "HYBRID_QUERY")]
    [InlineData("لخص توزيع الوثائق حسب الإدارة.", "group", "LASERFICHE_QUERY")]
    public void RoutesKeepMetadataAndContentSeparate(string question, string operation, string kind)
    {
        var plan = QuestionRouter.TryRoute(question);
        Assert.NotNull(plan);
        Assert.Equal(operation, plan.Operation);
        Assert.Equal(kind, plan.Kind.ToString());
    }

    [Fact]
    public async Task ExactCountUses73Not20AndDoesNotNeedDatabaseOrModel()
    {
        var query = new Searches();
        var entryService = new Entries(73);
        var service = Create(entryService, query);
        var result = await service.CreateAsync("repo", new QueryPlan("search"), [], default);
        Assert.Contains("**73**", result.Answer);
        Assert.Contains("ليست القائمة الكاملة", result.Answer);
        Assert.False(result.Scope!.Exhaustive);
        Assert.Equal(20, result.Sources.Count);
        Assert.False(entryService.Enumerated);
        Assert.Empty(entryService.FieldCalls);
        var config = new ConfigurationBuilder().Build();
        var chat = new ReportsChatService(config, new NoEmbeddings(), new Repository(), entryService,
            new NoClients(), service, new QuestionRouter(new NoClients()), query,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportsChatService>.Instance);
        Assert.Contains("**73**", (await chat.AskAsync("كم عدد الوثائق في المستودع؟", default)).Answer);
    }

    [Fact]
    public async Task ArabicFieldUsesAuthoritativeDefinitionAndServerFilter()
    {
        var query = new Searches();
        await Create(new Entries(73), query).CreateAsync("repo", new QueryPlan("search",
            "ماهي الوثائق التي إجراء الوثيقة", "تحت الإجراء"), [], default);
        Assert.Contains("{[]:[إجراء الوثيقة]=\"تحت الإجراء\"}", query.Expression);
        Assert.False(query.ReadAll);
    }

    [Theory]
    [InlineData("اعطيني الوثائق التي تحتوي على تحت الاجراء")]
    [InlineData("اعطيني الوثائق التي تحتوي على حالة الوثيقة = تحت الاجراء")]
    [InlineData("ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء")]
    public async Task ScreenshotQuestionsSearchDespiteRepeatedFieldDefinitions(string question)
    {
        var query = new Searches();
        var service = Create(new Entries(73), query, new Definitions("إجراء الوثيقة", "إجراء الوثيقة", "اجراء الوثيقة"));
        var plan = QuestionRouter.TryRoute(question)!;
        var result = await service.CreateAsync("repo", plan, [], default);
        Assert.Contains("{[]:[إجراء الوثيقة]=", query.Expression);
        Assert.Contains("**73**", result.Answer);
    }

    [Fact]
    public async Task SpellingVariantsWithoutLiteralMatchSearchAllAuthoritativeNames()
    {
        var query = new Searches();
        await Create(new Entries(73), query, new Definitions("إ جراء الوثيقة", "اجراء الوثيقة", "اجراء الوثيقة"))
            .SelectAsync(QuestionRouter.TryRoute("اعطيني الوثائق التي تحتوي على تحت الاجراء")!, [], false, default);
        Assert.Equal(LiveRepositoryReportService.Documents +
            " & ({[]:[إ جراء الوثيقة]=\"تحت الاجراء\"} | {[]:[اجراء الوثيقة]=\"تحت الاجراء\"})", query.Expression);
    }

    [Theory]
    [InlineData("تحت الاجراء")]
    [InlineData("تحت الإجراء")]
    public void ImplicitStatusPreservesTheRequestedValue(string value) =>
        Assert.Equal(value, QuestionRouter.TryRoute("اعطيني الوثائق التي تحتوي على " + value)!.Value);

    [Fact]
    public async Task LiteralStatusFieldWinsOverActionAlias()
    {
        var query = new Searches();
        await Create(new Entries(73), query, new Definitions("حالة الوثيقة", "إجراء الوثيقة"))
            .SelectAsync(QuestionRouter.TryRoute("اعطيني الوثائق التي تحتوي على حالة الوثيقة = تحت الاجراء")!, [], false, default);
        Assert.Contains("{[]:[حالة الوثيقة]=", query.Expression);
        Assert.DoesNotContain("[إجراء الوثيقة]", query.Expression);
    }

    [Fact]
    public async Task LiteralFieldWinsOverEquivalentSpellingsAndShorterSuffix()
    {
        var query = new Searches();
        await Create(new Entries(73), query, new Definitions("الوثيقة", "إجراء الوثيقة", "اجراء الوثيقة"))
            .SelectAsync(new QueryPlan("search", "اعرض اجراء الوثيقة", "تحت الاجراء"), [], false, default);
        Assert.Equal(LiveRepositoryReportService.Documents + " & {[]:[اجراء الوثيقة]=\"تحت الاجراء\"}", query.Expression);
    }

    [Fact]
    public async Task StatusAliasStillRequiresAnExistingLiveField()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create(new Entries(73), new Searches(), new Definitions("الإدارة"))
            .SelectAsync(new QueryPlan("search", "حالة الوثيقة", "تحت الاجراء"), [], false, default));
    }

    [Fact]
    public async Task UnknownFieldCannotBecomeZeroAndUnknownTotalCannotBecomeExact()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create(new Entries(73), new Searches())
            .CreateAsync("repo", new QueryPlan("search", "غير معروف", "قيمة"), [], default));
        var result = await Create(new Entries(73), new Searches { Exact = false })
            .CreateAsync("repo", new QueryPlan("search"), [], default);
        Assert.DoesNotContain("**73**", result.Answer);
        Assert.Contains("غير متاح", result.Answer);
    }

    [Fact]
    public async Task HybridSelectionUsesOnlyMatchingLiveIdsWithoutTraversal()
    {
        var entries = new Entries(73);
        var query = new Searches();
        var result = await Create(entries, query).SelectAsync(new QueryPlan("search",
            "إجراء الوثيقة", "تحت الإجراء", Content: true), [], true, default);
        Assert.True(query.ReadAll);
        Assert.Equal(73, result.Items.Count);
        Assert.False(entries.Enumerated);
    }

    [Theory]
    [InlineData("x\"} | {LF:Name=\"*")]
    [InlineData("*")]
    [InlineData("a\nb")]
    public void ToolArgumentsCannotInjectQuery(string value) =>
        Assert.Throws<ArgumentException>(() => LiveRepositoryReportService.Term(value));

    private static LiveRepositoryReportService Create(Entries entries, Searches search, Definitions? definitions = null) => new(entries, search,
        definitions ?? new Definitions(), new Templates(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveRepositoryReportService>.Instance);

    private sealed class Definitions(params string[] names) : ILaserficheFieldDefinitionService
    {
        public Task<IReadOnlyDictionary<int, LFFieldDefinition>> GetFieldDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<int, LFFieldDefinition>>((names.Length == 0 ? ["إجراء الوثيقة", "الإدارة"] : names)
                .Select((name, index) => new LFFieldDefinition { Id = index + 1, Name = name }).ToDictionary(f => f.Id));
    }
    private sealed class Templates : ILaserficheTemplateService
    {
        public Task<IReadOnlyList<LFTemplateDefinition>> GetTemplateDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LFTemplateDefinition>>([]);
    }
    private sealed class Searches : ILaserficheSearchService
    {
        public string Expression { get; private set; } = "";
        public bool ReadAll { get; private set; }
        public bool Exact { get; init; } = true;
        public Task<PagedResult<LFSearchResult>> QueryAsync(string expression, int page, int pageSize,
            string sort = "creationTime desc", string? field = null, bool readAll = false, CancellationToken cancellationToken = default)
        {
            Expression = expression; ReadAll = readAll;
            return Task.FromResult(new PagedResult<LFSearchResult> { Items = Enumerable.Range(1, readAll ? 73 : 20)
                .Select(i => new LFSearchResult { EntryId = i, Name = $"وثيقة {i}", EntryType = LFEntryType.Document }).ToArray(),
                TotalCount = 73, IsTotalCountExact = Exact, PageSize = pageSize, HasMore = !readAll });
        }
        public Task<PagedResult<LFSearchResult>> SimpleSearchAsync(string query, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<LFSearchResult>> AdvancedSearchAsync(string query, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<LFSearchResult>> SearchByTemplateAsync(string template, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<LFSearchResult>> SearchByFieldAsync(string field, string value, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
    }

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
