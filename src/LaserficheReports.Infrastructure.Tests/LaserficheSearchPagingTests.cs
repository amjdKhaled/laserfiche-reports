using System.Net;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class LaserficheSearchPagingTests
{
    [Fact]
    public async Task TaskFailureReadsStructuredApiErrors()
    {
        var http = new Responses("{\"taskId\":\"task\"}",
            "{\"value\":[{\"id\":\"task\",\"status\":\"Failed\",\"errors\":[{\"title\":\"Invalid search\",\"detail\":\"Invalid field\",\"errorCode\":9010}]}]}");
        var error = await Assert.ThrowsAsync<LaserficheReports.Domain.Exceptions.LaserficheException>(
            () => Create(http).QueryAsync("trusted", 1, 20));
        Assert.Contains("Invalid field", error.Message);
        Assert.Contains("9010", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"count\":0}")]
    public async Task MalformedResultsAreNotReportedAsZeroMatches(string body)
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}", body);
        await Assert.ThrowsAsync<JsonException>(() => Create(http).QueryAsync("trusted", 1, 20));
    }

    [Fact]
    public async Task FieldSyntaxIsWellFormedAndRowsThatEndBeforeThePageAreTheTotal()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":1,\"value\":[{\"id\":1,\"name\":\"مستند\",\"entryType\":\"Document\",\"pageCount\":9}]}");
        var result = await Create(http).SearchByFieldAsync("إجراء الوثيقة", "تحت الإجراء", 1, 20);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(9, result.Items[0].PageCount);
        Assert.True(result.IsTotalCountExact);
        Assert.False(result.HasNextPage);
        Assert.Equal("{[]:[إجراء الوثيقة]=\"تحت الإجراء\"}", JsonDocument.Parse(http.Body!).RootElement.GetProperty("searchCommand").GetString());
        Assert.Contains("$count=true", http.Urls[1]);
        Assert.Contains("$top=21", http.Urls[1]);
    }

    [Fact]
    public async Task FullPageIsNotReportedAsTheTotalEvenWhenTheServerCountsIt()
    {
        // 2026-10-28: Laserfiche answered $top=50 with 50 rows and @odata.count=50 while 444 documents matched.
        var rows = string.Join(",", Enumerable.Range(1, 51).Select(id => $"{{\"id\":{id}}}"));
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}", $"{{\"@odata.count\":51,\"value\":[{rows}]}}");
        var result = await Create(http).QueryAsync("trusted", 1, 50);
        Assert.Equal(50, result.Items.Count);
        Assert.False(result.IsTotalCountExact);
        Assert.True(result.HasNextPage);
        Assert.Contains("$top=51", http.Urls[1]);
    }

    [Fact]
    public async Task PageCountLargerThanTheRowsThatEndedIsNotAnExactTotal()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":73,\"value\":[{\"id\":1}]}");
        var result = await Create(http).QueryAsync("trusted", 1, 20);
        Assert.False(result.IsTotalCountExact);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public async Task MissingCountWithContinuationIsNotAnInventedTotal()
    {
        var http = new Responses("{\"value\":[{\"id\":1}],\"@odata.nextLink\":\"?next=1\"}");
        var result = await Create(http).AdvancedSearchAsync("trusted", 1, 1);
        Assert.False(result.IsTotalCountExact);
        Assert.True(result.HasNextPage);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task PageTwoDoesNotSkipRowsTwiceOrClaimPageCountIsTotal()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":2,\"value\":[{\"id\":21},{\"id\":22}]}");
        var result = await Create(http).QueryAsync("trusted", 2, 20);
        Assert.Equal(new[] { 21, 22 }, result.Items.Select(i => i.EntryId));
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(22, result.TotalCount);
        Assert.True(result.IsTotalCountExact);
        Assert.False(result.HasNextPage);
        Assert.Contains("$skip=20", http.Urls[1]);
    }

    [Fact]
    public async Task CompleteSelectionSubmitsOneSearchAndFollowsContinuationWithoutTopCap()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":2,\"value\":[{\"id\":1}],\"@odata.nextLink\":\"?next=1\"}",
            "{\"value\":[{\"id\":2}]}");
        var result = await Create(http).QueryAsync("trusted", 1, 1, field: "الإدارة", readAll: true);
        Assert.Equal(2, result.Items.Count);
        Assert.False(result.HasNextPage);
        Assert.Equal(1, http.Posts);
        Assert.DoesNotContain("$top", http.Urls[1]);
        Assert.Contains("fields=", http.Urls[1]);
    }

    [Fact]
    public async Task IncompleteCompleteSelectionFailsInsteadOfWrongAggregation()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":73,\"value\":[{\"id\":1}]}");
        await Assert.ThrowsAsync<JsonException>(() => Create(http).QueryAsync("trusted", 1, 20, readAll: true));
    }

    [Fact]
    public async Task CompleteReadAcceptsRowsTheServerCountedTwiceForOneEntry()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":3,\"value\":[{\"id\":1},{\"id\":2}],\"@odata.nextLink\":\"?next=1\"}",
            "{\"value\":[{\"id\":2}]}");
        var result = await Create(http).QueryAsync("trusted", 1, 20, readAll: true);
        Assert.Equal(new[] { 1, 2 }, result.Items.Select(i => i.EntryId));
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.IsTotalCountExact);
    }

    [Fact]
    public async Task CompleteReadTrustsDeliveredRowsWhenTheServerCountsOnlyTheFirstPage()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":2,\"value\":[{\"id\":1},{\"id\":2}],\"@odata.nextLink\":\"?next=1\"}",
            "{\"@odata.count\":1,\"value\":[{\"id\":3}]}");
        var result = await Create(http).QueryAsync("trusted", 1, 20, readAll: true);
        Assert.Equal(new[] { 1, 2, 3 }, result.Items.Select(i => i.EntryId));
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.IsTotalCountExact);
    }

    [Fact]
    public async Task CompleteReadRequestsLargePagesAndPagedReadKeepsItsPageSize()
    {
        var complete = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}", "{\"@odata.count\":1,\"value\":[{\"id\":1}]}");
        await Create(complete).QueryAsync("trusted", 1, 20, readAll: true);
        Assert.Contains("odata.maxpagesize=1000", complete.Prefers);
        var paged = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}", "{\"@odata.count\":1,\"value\":[{\"id\":1}]}");
        await Create(paged).QueryAsync("trusted", 1, 20);
        Assert.Contains("odata.maxpagesize=21", paged.Prefers);
    }

    [Fact]
    public async Task MultipleFieldProjectionUsesRepeatedParametersAndAscendingSort()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}", "{\"@odata.count\":0,\"value\":[]}");
        await Create(http).QueryAsync("trusted", 1, 20, sort: "creationTime asc", projectedFields: ["القسم", "قيمة الطلب"]);
        var url = Uri.UnescapeDataString(http.Urls[1]);
        Assert.Contains("&fields=القسم&fields=قيمة الطلب", url);
        Assert.Contains("$orderby=creationTime asc", url);
    }

    [Fact]
    public async Task OneSessionRunsOneSearchAtATime()
    {
        // The report button and a question searched together in one session on 2026-10-28; Laserfiche failed both.
        var http = new OneSearchAtATime();
        await Task.WhenAll(
            new LaserficheSearchService(http, new Repository(), new LaserficheApiAdapter(new Monitor()), new InMemorySearchAuditLog(), NullLogger<LaserficheSearchService>.Instance).QueryAsync("trusted", 1, 20),
            new LaserficheSearchService(http, new Repository(), new LaserficheApiAdapter(new Monitor()), new InMemorySearchAuditLog(), NullLogger<LaserficheSearchService>.Instance).QueryAsync("trusted", 1, 20));
        Assert.Equal(2, http.Searches);
        Assert.Equal(1, http.MostAtOnce);
    }

    private static LaserficheSearchService Create(Responses http) => new(http, new Repository(),
        new LaserficheApiAdapter(new Monitor()), new InMemorySearchAuditLog(), NullLogger<LaserficheSearchService>.Instance);
    private sealed class OneSearchAtATime : HttpMessageHandler, IHttpClientFactory
    {
        private readonly object sync = new();
        private int running;
        public int Searches { get; private set; }
        public int MostAtOnce { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body;
            if (request.Method == HttpMethod.Post)
            {
                lock (sync) { Searches++; MostAtOnce = Math.Max(MostAtOnce, ++running); }
                body = "{\"taskId\":\"task\"}";
            }
            else if (request.RequestUri!.AbsolutePath.EndsWith("/Tasks", StringComparison.Ordinal))
            {
                await Task.Delay(50, ct);
                body = "{\"value\":[{\"id\":\"task\",\"status\":\"Completed\"}]}";
            }
            else
            {
                lock (sync) running--;
                body = "{\"value\":[{\"id\":1}]}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
    private sealed class Repository : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) =>
            Task.FromResult(new RepositoryDescriptor("repo", "https://lf.test", "repo", "repo"));
        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Monitor : IOptionsMonitor<LaserficheOptions>
    {
        public LaserficheOptions CurrentValue { get; } = new() { ServerUrl = "https://lf.test", ApiVersion = "v2" };
        public LaserficheOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LaserficheOptions, string?> listener) => null;
    }
    private sealed class Responses(params string[] bodies) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Urls { get; } = [];
        public List<string> Prefers { get; } = [];
        public string? Body { get; private set; }
        public int Posts { get; private set; }
        private readonly Queue<string> remaining = new(bodies);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            if (request.Headers.TryGetValues("Prefer", out var prefer)) Prefers.AddRange(prefer);
            if (request.Method == HttpMethod.Post) { Posts++; Body = await request.Content!.ReadAsStringAsync(ct); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(remaining.Dequeue()) };
        }
    }
}
