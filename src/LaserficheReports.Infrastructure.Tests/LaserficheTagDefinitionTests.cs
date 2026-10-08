using System.Net;
using System.Text.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public class LaserficheTagDefinitionTests
{
    [Fact]
    public async Task EveryPageAndEveryRequestUsesCurrentRepositoryTags()
    {
        var http = new Responses(
            "{\"value\":[{\"id\":1,\"name\":\"قيد الفحص\"}],\"@odata.nextLink\":\"?page=2\"}",
            "{\"value\":[{\"id\":2,\"name\":\"معتمد\"}]}",
            "[{\"id\":3,\"name\":\"وسم جديد\",\"description\":\"تعريف محدث\"}]");
        var service = Create(http);
        var tags = await service.GetTagDefinitionsAsync();
        Assert.Equal(new[] { "قيد الفحص", "معتمد" }, tags.Select(t => t.Name));
        Assert.Equal(2, http.Urls.Count);
        Assert.EndsWith("/TagDefinitions?page=2", http.Urls[1]);
        var changed = await service.GetTagDefinitionsAsync();
        Assert.Single(changed);
        Assert.Equal("وسم جديد", changed[0].Name);
        Assert.Equal("تعريف محدث", changed[0].Description);
        Assert.All(http.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData("https://outside.test/TagDefinitions")]
    [InlineData("/LFRepositoryAPI/v2/Repositories/other/TagDefinitions")]
    [InlineData("/LFRepositoryAPI/v2/Repositories/repo/Entries")]
    public async Task ContinuationCannotLeaveAuthenticatedRepositoryCollection(string link)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = link });
        var http = new Responses(body);
        await Assert.ThrowsAsync<JsonException>(() => Create(http).GetTagDefinitionsAsync());
        Assert.Single(http.Urls);
    }

    [Fact]
    public async Task MissingDefinitionOrFailedReadIsNotAnEmptySuccessfulCatalog()
    {
        await Assert.ThrowsAsync<JsonException>(() => Create(new Responses("{\"value\":[{\"id\":1}]}" )).GetTagDefinitionsAsync());
        var http = new Responses("{}") { Status = HttpStatusCode.Unauthorized };
        var error = await Assert.ThrowsAsync<LaserficheException>(() => Create(http).GetTagDefinitionsAsync());
        Assert.Equal(401, error.StatusCode);
        Assert.Single(http.Urls);
    }

    private static LaserficheTagDefinitionService Create(Responses http) => new(http, new Repository(),
        new LaserficheApiAdapter(new Monitor()), NullLogger<LaserficheTagDefinitionService>.Instance);
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
        private readonly Queue<string> remaining = new(bodies);
        public List<string> Urls { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString()); Methods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(remaining.Dequeue()) });
        }
    }
}
