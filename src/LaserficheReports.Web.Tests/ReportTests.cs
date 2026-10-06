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
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.AskAsync("كم عدد الوثائق في المستودع؟", default));
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

    [Fact]
    public async Task RepeatedFieldDefinitionsDoNotPreventValidatedFieldSearch()
    {
        var query = new Searches();
        var service = Create(new Entries(73), query, new Definitions("إجراء الوثيقة", "إجراء الوثيقة", "اجراء الوثيقة"));
        var plan = new QueryPlan("search", "إجراء الوثيقة", "تحت الاجراء");
        var result = await service.CreateAsync("repo", plan, [], default);
        Assert.Contains("{[]:[إجراء الوثيقة]=", query.Expression);
        Assert.Contains("**73**", result.Answer);
    }

    [Fact]
    public async Task SpellingVariantsWithoutLiteralMatchSearchAllAuthoritativeNames()
    {
        var query = new Searches();
        await Create(new Entries(73), query, new Definitions("إ جراء الوثيقة", "اجراء الوثيقة", "اجراء الوثيقة"))
            .SelectAsync(new QueryPlan("search", "إجراء الوثيقة", "تحت الاجراء"), [], false, default);
        Assert.Equal(LiveRepositoryReportService.Documents +
            " & ({[]:[إ جراء الوثيقة]=\"تحت الاجراء\"} | {[]:[اجراء الوثيقة]=\"تحت الاجراء\"})", query.Expression);
    }

    [Fact]
    public async Task LiteralStatusFieldWinsOverActionAlias()
    {
        var query = new Searches();
        await Create(new Entries(73), query, new Definitions("حالة الوثيقة", "إجراء الوثيقة"))
            .SelectAsync(new QueryPlan("search", "حالة الوثيقة", "تحت الاجراء"), [], false, default);
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

    private static PagedResult<LFSearchResult> FieldRows(string field, params string[] values) => new()
    {
        Items = values.Select((value, i) => new LFSearchResult { EntryId = i + 1, Name = "وثيقة " + (i + 1),
            Fields = [new LFSearchField(field, [value], false)] }).ToArray(),
        TotalCount = values.Length, HasMore = false
    };

    [Fact]
    public async Task ZeroLiteralResultsCheckLiveValuesWithoutMatchingNegativeOrLongerValues()
    {
        var entries = new Entries(73);
        var query = new Searches { Response = (expression, field, _) => expression.Contains("=\"*\"}")
            ? FieldRows(field!, "تحت الإجراء", "تحت  الاجراء", "ليس تحت الإجراء", "تحت الإجراء النهائي", "تم الرفض")
            : PagedResult<LFSearchResult>.Empty };
        var result = await Create(entries, query).CreateAsync("repo",
            new QueryPlan("search", "إجراء الوثيقة", "تحت الاجراء"), [], default);
        Assert.Contains("**2**", result.Answer);
        Assert.Equal(new[] { 1, 2 }, result.RelatedEntryIds);
        Assert.True(query.ReadAll);
        Assert.Empty(entries.FieldCalls);
        Assert.False(entries.Enumerated);
    }

    [Fact]
    public async Task ValueCheckPreservesScopeCountsAllMatchesAndDeduplicatesEquivalentFields()
    {
        var query = new Searches { Response = (expression, field, _) => expression.Contains("=\"*\"}")
            ? FieldRows(field!, "تحت الإجراء", "تحت الاجراء", "تم الرفض") : PagedResult<LFSearchResult>.Empty };
        var plan = new QueryPlan("created", "إجراء الوثيقة", "تحت الاجراء", Name: "طلب", Limit: 1,
            From: "2026-10-01", To: "2026-10-06");
        var result = await Create(new Entries(73), query, new Definitions("إجراء الوثيقة", "اجراء الوثيقة"))
            .SelectAsync(plan, [1, 2], false, default);
        Assert.Equal(2, result.TotalCount);
        Assert.Single(result.Items);
        Assert.True(result.HasNextPage);
        Assert.Equal(3, query.Calls.Count);
        Assert.All(query.Calls, call =>
        {
            Assert.Contains("{LF:ID=1} | {LF:ID=2}", call);
            Assert.Contains("{LF:Name=\"طلب\", Type=D}", call);
            Assert.Contains("{LF:Created>=\"2026-10-01\"}", call);
            Assert.Contains("{LF:Created<=\"2026-10-06\"}", call);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrIncompleteLiveValuesCannotBeReportedAsZero(bool incomplete)
    {
        var query = new Searches { Response = (expression, field, _) => expression.Contains("=\"*\"}")
            ? new PagedResult<LFSearchResult> { Items = [new LFSearchResult { EntryId = 1 }],
                TotalCount = 1, HasMore = incomplete } : PagedResult<LFSearchResult>.Empty };
        await Assert.ThrowsAsync<ArgumentException>(() => Create(new Entries(73), query)
            .SelectAsync(new QueryPlan("search", "إجراء الوثيقة", "تحت الاجراء"), [], false, default));
    }

    [Fact]
    public async Task TruncatedMultiValueProjectionReadsTheFullLiveFieldBeforeMatching()
    {
        var entries = new Entries(73);
        var query = new Searches { Response = (expression, field, _) => expression.Contains("=\"*\"}")
            ? new PagedResult<LFSearchResult> { Items = [new LFSearchResult { EntryId = 1,
                Fields = [new LFSearchField(field!, ["رفض"], true)] }], TotalCount = 1, HasMore = false }
            : PagedResult<LFSearchResult>.Empty };
        var result = await Create(entries, query).SelectAsync(new QueryPlan("search", "إجراء الوثيقة", "تحت الاجراء"), [], true, default);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(new[] { 1 }, entries.FieldCalls);
    }

    [Fact]
    public async Task ValueCheckKeepsTheGroupingFieldSeparateFromTheFilterField()
    {
        var query = new Searches { Response = (expression, field, _) => expression.Contains("=\"*\"}")
            ? FieldRows(field!, "تحت الإجراء") : PagedResult<LFSearchResult>.Empty };
        var result = await Create(new Entries(73), query).CreateAsync("repo",
            new QueryPlan("group", "إجراء الوثيقة", "تحت الاجراء", GroupBy: "الإدارة"), [], default);
        Assert.Contains("الإدارة العامة | 1", result.Answer);
        Assert.DoesNotContain("تحت الإجراء |", result.Answer);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AiPlansTwoIndependentLatestReportsWithOneRowAndDifferentOrdering(bool presentationUnavailable)
    {
        var graph = new GraphClient("""
            {"reports":[
                {"operation":"latest_modified","title":"آخر وثيقة معدلة","question":"آخر تعديل","limit":1,"entryIds":[]},
                {"operation":"latest_created","title":"آخر وثيقة منشأة","question":"آخر إنشاء","limit":1,"entryIds":[]}]}
            """, presentationUnavailable);
        var index = 0;
        var query = new Searches { Response = (_, _, _) => new PagedResult<LFSearchResult>
        {
            Items = [new LFSearchResult { EntryId = ++index == 1 ? 42 : 619, Name = index == 1 ? "وثيقة معدلة" : "وثيقة جديدة",
                CreationTime = DateTimeOffset.Parse("2026-10-01T10:00:00+03:00"), LastModifiedTime = DateTimeOffset.Parse("2026-10-06T11:00:00+03:00") }],
            TotalCount = 73, HasMore = true
        } };
        var entryService = new Entries(73);
        var chat = new ReportsChatService(new ConfigurationBuilder().Build(), new NoEmbeddings(), new Repository(),
            entryService, graph, Create(entryService, query), new QuestionRouter(graph), query,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportsChatService>.Instance);
        var result = await chat.AskAsync("اعطيني تقرير عن آخر وثيقة تم تعديلها وتقرير آخر عن آخر وثيقة أُنشئت", default);
        Assert.Equal(2, result.Reports.Count);
        if (presentationUnavailable) Assert.All(result.Reports, report => Assert.Contains("تعذرت صياغة ملخص AI", report.Answer));
        Assert.Equal(new[] { "lastModifiedTime desc", "creationTime desc" }, query.Sorts);
        Assert.Equal(new[] { 1, 1 }, query.Limits);
        Assert.Equal(new[] { 42 }, result.Reports[0].RelatedEntryIds);
        Assert.Equal(new[] { 619 }, result.Reports[1].RelatedEntryIds);
        Assert.All(result.Reports, report =>
        {
            Assert.Single(report.Sources);
            Assert.StartsWith("\\قسم\\وثيقة", report.Sources[0].Path);
            Assert.Contains("2026-10-06 11:00:00 +03:00", report.Answer);
            Assert.DoesNotContain("**73**", report.Answer);
        });
        Assert.Equal(new[] { "route", "present" }, graph.Paths);
        Assert.Contains("إجراء الوثيقة", System.Text.RegularExpressions.Regex.Unescape(graph.RouteBody));
        Assert.DoesNotContain("[1] |", result.Answer.Split("آخر وثيقة منشأة").Last());
    }

    [Theory]
    [InlineData("{\"reports\":[{\"operation\":\"http\",\"limit\":1}]}")]
    [InlineData("{\"reports\":[{\"operation\":\"latest_created\",\"limit\":10}]}")]
    [InlineData("{\"reports\":[{\"operation\":\"metadata\",\"limit\":1,\"entryIds\":[42]}]}")]
    public async Task InvalidAiPlansCannotExecuteTools(string plan)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new QuestionRouter(new GraphClient(plan))
            .RouteAsync("تقرير المستودع", new { fields = Array.Empty<string>() }, default));
    }

    private sealed class GraphClient(string route, bool failPresentation = false) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Paths { get; } = [];
        public string RouteBody { get; private set; } = "";
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("http://graph.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/'); Paths.Add(path);
            var body = await request.Content!.ReadAsStringAsync(ct);
            string response;
            if (path == "route") { RouteBody = body; response = route; }
            else
            {
                if (failPresentation) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
                using var data = System.Text.Json.JsonDocument.Parse(body);
                response = System.Text.Json.JsonSerializer.Serialize(new { reports = data.RootElement.GetProperty("reports").EnumerateArray()
                    .Select(item => new { index = item.GetProperty("index").GetInt32(), summary = "هذه نتيجة التقرير من بيانات المستودع الحالية." }).ToArray() });
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
    }

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
        public List<string> Calls { get; } = [];
        public List<string> Sorts { get; } = [];
        public List<int> Limits { get; } = [];
        public Func<string, string?, bool, PagedResult<LFSearchResult>>? Response { get; init; }
        public Task<PagedResult<LFSearchResult>> QueryAsync(string expression, int page, int pageSize,
            string sort = "creationTime desc", string? field = null, bool readAll = false, CancellationToken cancellationToken = default)
        {
            Expression = expression; ReadAll = readAll;
            Calls.Add(expression); Sorts.Add(sort); Limits.Add(pageSize);
            if (Response is not null) return Task.FromResult(Response(expression, field, readAll));
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
                new() { FieldName = "الإدارة", Value = "الإدارة العامة" },
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
