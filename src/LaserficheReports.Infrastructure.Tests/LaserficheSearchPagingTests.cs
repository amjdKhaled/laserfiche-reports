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
    public async Task FieldSyntaxIsWellFormedAndCountIsServerTotal()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}",
            "{\"@odata.count\":73,\"value\":[{\"id\":1,\"name\":\"مستند\",\"entryType\":\"Document\",\"pageCount\":9}]}");
        var result = await Create(http).SearchByFieldAsync("إجراء الوثيقة", "تحت الإجراء", 1, 20);
        Assert.Equal(73, result.TotalCount);
        Assert.Equal(9, result.Items[0].PageCount);
        Assert.True(result.IsTotalCountExact);
        Assert.True(result.HasNextPage);
        Assert.Equal("{[]:[إجراء الوثيقة]=\"تحت الإجراء\"}", JsonDocument.Parse(http.Body!).RootElement.GetProperty("searchCommand").GetString());
        Assert.Contains("$count=true", http.Urls[1]);
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
            "{\"@odata.count\":73,\"value\":[{\"id\":21},{\"id\":22}]}");
        var result = await Create(http).QueryAsync("trusted", 2, 20);
        Assert.Equal(new[] { 21, 22 }, result.Items.Select(i => i.EntryId));
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(73, result.TotalCount);
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
    public async Task MultipleFieldProjectionUsesRepeatedParametersAndAscendingSort()
    {
        var http = new Responses("{\"taskId\":\"task\",\"status\":\"Completed\"}", "{\"@odata.count\":0,\"value\":[]}");
        await Create(http).QueryAsync("trusted", 1, 20, sort: "creationTime asc", projectedFields: ["القسم", "قيمة الطلب"]);
        var url = Uri.UnescapeDataString(http.Urls[1]);
        Assert.Contains("&fields=القسم&fields=قيمة الطلب", url);
        Assert.Contains("$orderby=creationTime asc", url);
    }

    private static LaserficheSearchService Create(Responses http) => new(http, new Repository(),
        new LaserficheApiAdapter(new Monitor()), new InMemorySearchAuditLog(), NullLogger<LaserficheSearchService>.Instance);
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
        public string? Body { get; private set; }
        public int Posts { get; private set; }
        private readonly Queue<string> remaining = new(bodies);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            if (request.Method == HttpMethod.Post) { Posts++; Body = await request.Content!.ReadAsStringAsync(ct); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(remaining.Dequeue()) };
        }
    }
}
