using System.Net;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public class LaserficheEntryPathTests
{
    [Fact]
    public async Task NullSearchPathsAreReconstructedFromLiveAncestorsAndSharedParentsAreReused()
    {
        var http = new Responses(
            "{\"id\":42,\"name\":\"قرار\",\"parentId\":7,\"fullPath\":null,\"entryType\":\"Document\"}",
            "{\"id\":7,\"name\":\"الإدارة\",\"parentId\":1,\"fullPath\":null,\"entryType\":\"Folder\"}",
            "{\"id\":1,\"name\":\"المستودع\",\"parentId\":0,\"fullPath\":null,\"entryType\":\"Folder\"}",
            "{\"id\":43,\"name\":\"طلب\",\"parentId\":7,\"fullPath\":null,\"entryType\":\"Document\"}");
        var service = Create(http);
        Assert.Equal("\\الإدارة\\قرار", await service.GetEntryPathAsync(42));
        Assert.Equal("\\الإدارة\\طلب", await service.GetEntryPathAsync(43));
        Assert.Equal(4, http.Calls);
        Assert.Equal("\\الإدارة\\قرار", await service.GetEntryPathAsync(42));
        Assert.Equal(4, http.Calls);
    }
    [Fact]
    public async Task CyclicParentsCannotProduceAFalsePath()
    {
        var http = new Responses("{\"id\":2,\"name\":\"أ\",\"parentId\":3}",
            "{\"id\":3,\"name\":\"ب\",\"parentId\":2}", "{\"id\":2,\"name\":\"أ\",\"parentId\":3}");
        await Assert.ThrowsAsync<LaserficheException>(() => Create(http).GetEntryPathAsync(2));
    }
    private static LaserficheEntryService Create(Responses http) => new(http, new Repository(),
        new LaserficheApiAdapter(new Monitor()), NullLogger<LaserficheEntryService>.Instance);
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
        public int Calls { get; private set; }
        private readonly Queue<string> remaining = new(bodies);
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(remaining.Dequeue()) });
        }
    }
}
