using System.Net;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Web.Permissions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class ReportAuthorizationReceiptTests
{
    private sealed class Principal : IAuthenticatedLaserfichePrincipalResolver
    {
        public string Scope = "user-a";
        public Task<AuthenticatedPermissionPrincipal?> ResolveAsync(PermissionRepositoryBinding binding, CancellationToken ct) =>
            Task.FromResult<AuthenticatedPermissionPrincipal?>(new(Scope, Scope, null, "sid"));
    }
    private sealed class Repository : IRepositoryContext
    {
        public string Server = "https://lf";
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) => Task.FromResult(new RepositoryDescriptor("same-key", Server, "repo", "repo"));
        public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) => new[] { await GetActiveRepositoryAsync(ct) };
    }
    private sealed class Gateway : IFullHierarchyAuthorizationService
    {
        public int[] AuthorizedEntryIds => [553];
        public bool Allowed = true;
        public int Calls;
        public Task<bool> CanReadAsync(int id, CancellationToken ct) { Calls++; return Task.FromResult(Allowed); }
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public string Body = "{\"value\":[]}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
    [Fact]
    public async Task ReceiptsRecheckRightsAndRejectAnotherUserRepositoryOrExpiredToken()
    {
        using var store = new AuthorizationReceiptCache();
        var principal = new Principal(); var repository = new Repository(); var gateway = new Gateway();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["PermissionVerification:Repositories:repo:ApiServerUrl"] = "https://lf" }).Build();
        var service = new ReportAuthorizationReceipts(store, repository, principal, config, gateway,
            new RecycleBinSource(config, repository, null!), new ProtectedReadTracker(), new Transport());
        var key = await service.IssueAsync(new ChatResult("protected", []), default);
        Assert.Equal(1, gateway.Calls); // Post-model check.
        Assert.True(await service.ValidateAsync(key, default)); Assert.Equal(2, gateway.Calls);
        gateway.Allowed = false; Assert.False(await service.ValidateAsync(key, default));
        gateway.Allowed = true; principal.Scope = "user-b"; Assert.False(await service.ValidateAsync(key, default));
        principal.Scope = "user-a"; repository.Server = "https://other"; Assert.False(await service.ValidateAsync(key, default));
        Assert.False(await service.ValidateAsync(new string('0', 64), default));
        Assert.False(await service.ValidateAsync(null, default));
    }
    [Fact]
    public async Task FieldChangesAndActualContentDenialInvalidateStoredEvidence()
    {
        var transport = new Transport(); var reads = new ProtectedReadTracker();
        using (var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(transport.Body) })
            await reads.RecordAsync(new Uri("https://lf/Repositories/repo/Entries/553/fields"), response, true, default);
        Assert.True(await ProtectedReadTracker.RevalidateAsync(reads.Snapshot(), transport, default));
        transport.Body = "{\"value\":[\"changed or restricted field\"]}";
        Assert.False(await ProtectedReadTracker.RevalidateAsync(reads.Snapshot(), transport, default));
        transport.Status = HttpStatusCode.Forbidden;
        Assert.False(await ProtectedReadTracker.RevalidateAsync(reads.Snapshot(), transport, default));
    }
}
