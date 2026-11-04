using System.Net;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Web.Permissions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class HierarchyAuthorizationHandlerTests
{
    private sealed class Gateway : IFullHierarchyAuthorizationService
    {
        public readonly List<int> Checked = [];
        public int[] AuthorizedEntryIds => [];
        public bool Unknown;
        public Task<bool> CanReadAsync(int id, CancellationToken ct)
        {
            lock (Checked) Checked.Add(id);
            if (Unknown) throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable));
            return Task.FromResult(id != 553);
        }
    }
    private sealed class Repository : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) =>
            Task.FromResult(new RepositoryDescriptor("key", "https://lf", "repo", "Repository"));
        public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) =>
            new[] { await GetActiveRepositoryAsync(ct) };
    }
    private sealed class Transport(string body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }); }
    }
    private static HttpClient Client(Gateway gateway, Transport transport)
    {
        var services = new ServiceCollection().AddSingleton<IFullHierarchyAuthorizationService>(gateway)
            .AddSingleton<IRepositoryContext, Repository>().AddSingleton<ProtectedReadTracker>().BuildServiceProvider();
        var contexts = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = services } };
        return new HttpClient(new HierarchyAuthorizationHandler(contexts) { InnerHandler = transport });
    }
    [Theory]
    [InlineData("Searches/task/Results")]
    [InlineData("Entries/18/Folder/Children")]
    public async Task HiddenRowsAndCandidateCountsNeverReachConsumers(string path)
    {
        var gateway = new Gateway(); var transport = new Transport("""
            {"value":[{"id":553,"name":"SECRET","fields":["PRIVATE"]},{"id":475,"name":"visible"}],
              "@odata.count":100,"totalCount":100,"count":100,"@odata.nextLink":"?page=2"}
            """);
        using var client = Client(gateway, transport);
        var body = await client.GetStringAsync("https://lf/Repositories/repo/" + path);
        Assert.DoesNotContain("SECRET", body); Assert.DoesNotContain("PRIVATE", body);
        Assert.DoesNotContain("count", body); Assert.Contains("nextLink", body); Assert.Contains("visible", body);
        Assert.Contains(553, gateway.Checked); Assert.Contains(475, gateway.Checked);
    }
    [Fact]
    public async Task DirectContentIsDeniedBeforeTransportStarts()
    {
        var transport = new Transport("SECRET BYTES"); using var client = Client(new(), transport);
        await Assert.ThrowsAsync<PermissionVerificationException>(() => client.GetAsync("https://lf/Repositories/repo/Entries/553/Document/Edoc"));
        Assert.Equal(0, transport.Calls);
    }
    [Fact]
    public async Task UnknownCandidateInvalidatesEntirePage()
    {
        using var client = Client(new() { Unknown = true }, new("{\"value\":[{\"id\":475,\"name\":\"SECRET\"}]}"));
        await Assert.ThrowsAsync<PermissionVerificationException>(() => client.GetAsync("https://lf/Repositories/repo/Searches/task/Results"));
    }
    [Theory]
    [InlineData("https://other/Repositories/repo/Entries/475")]
    [InlineData("https://lf/Repositories/other/Entries/475")]
    public async Task WrongHostOrRepositoryIsRejectedBeforeTokenOrContentUse(string url)
    {
        var transport = new Transport("SECRET"); using var client = Client(new(), transport);
        await Assert.ThrowsAsync<PermissionVerificationException>(() => client.GetAsync(url));
        Assert.Equal(0, transport.Calls);
    }
    [Fact]
    public async Task MalformedCandidateCannotBeInterpretedAsEmptyOrAllowed()
    {
        using var client = Client(new(), new("{\"value\":[{\"name\":\"SECRET\"}]}"));
        await Assert.ThrowsAsync<PermissionVerificationException>(() => client.GetAsync("https://lf/Repositories/repo/Searches/task/Results"));
    }
}
