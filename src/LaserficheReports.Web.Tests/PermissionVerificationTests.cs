using System.Text;
using System.Text.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Web.Permissions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class PermissionVerificationTests
{
    private static string Reply(string status = "DocumentAccessDenied", string? kind = "document",
        string repository = "A", string scope = "scope", string request = "request") => JsonSerializer.Serialize(new {
        protocol = "permission-v1", requestId = request, repositoryId = repository, principalScope = scope, status, entryKind = kind });

    [Fact]
    public void BridgeRejectsCrossUserCrossRepositoryReplaysAndExtraSensitiveData()
    {
        Assert.Equal(PermissionStatus.DocumentAccessDenied, ProcessLaserficheSdkBridge.Parse(Reply(), "request", "A", "scope").Status);
        foreach (var json in new[] { Reply(repository: "B"), Reply(scope: "another-user"), Reply(request: "replay"),
            Reply(status: "999"), Reply(kind: "folder"), Reply("NotFound", null),
            Reply()[..^1] + ",\"path\":\"secret\"}", "not json" })
            Assert.Equal(PermissionStatus.VerificationUnavailable, ProcessLaserficheSdkBridge.Parse(json, "request", "A", "scope").Status);
    }

    [Theory]
    [InlineData("DocumentAccessDenied", "لا توجد لديك صلاحية على هذه الوثيقة.")]
    [InlineData("FolderAccessDenied", "لا توجد لديك صلاحية على هذا المجلد.")]
    [InlineData("Unknown", PermissionDecision.Uncertain)]
    [InlineData("VerificationUnavailable", PermissionDecision.Uncertain)]
    public void PublicStatusHasNoSourcesLinksOrProtectedDetails(string status, string message)
    {
        var result = new PermissionDecision(Enum.Parse<PermissionStatus>(status)).ToChatResult();
        Assert.Equal(message, result.Answer);
        Assert.Empty(result.Sources); Assert.Empty(result.RelatedEntryIds);
        Assert.Null(result.Scope);
    }

    [Fact]
    public void TargetRejectsBroadSearchesAndMixedIdentitySelectors()
    {
        foreach (var target in new[] { new PermissionTarget(), new(Id: 0), new(Id: 1, Name: "file"),
            new(Name: "*"), new(Name: "a\" | {LF:ID=2}"), new(Path: "relative"), new(Name: "a\n"), new(Id: 1, Kind: "secret") })
            Assert.False(target.IsValid);
        Assert.True(new PermissionTarget(Id: 2).IsValid);
        Assert.True(new PermissionTarget(Name: "وثيقة محددة").IsValid);
        Assert.True(new PermissionTarget(Path: "\\قسم\\وثيقة").IsValid);
    }

    [Fact]
    public void DisplayNameCannotSubstituteForStableSsoIdentity()
    {
        string Token(object data) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))).TrimEnd('=').Replace('+','-').Replace('/','_') + ".signature";
        Assert.Null(AuthenticatedLaserfichePrincipalResolver.ReadSubject(Token(new { name = "Admin", preferred_username = "Admin" })));
        Assert.Null(AuthenticatedLaserfichePrincipalResolver.ReadSubject("opaque"));
        Assert.Null(AuthenticatedLaserfichePrincipalResolver.ReadSubject(Token(new { iss = "issuer", sub = 5 })));
        Assert.Equal(("issuer", "stable-id"), AuthenticatedLaserfichePrincipalResolver.ReadSubject(Token(new { iss = "issuer", sub = "stable-id", name = "same display name" })));
    }

    [Fact]
    public async Task DisabledOrWrongServerNeverStartsPrivilegedBridge()
    {
        var principal = new Principals(); var bridge = new Bridge(); using var gate = new PermissionVerificationGate();
        var disabled = Service(false, "http://fixture", principal, bridge, gate);
        Assert.Equal(PermissionStatus.Unknown, (await disabled.VerifyAsync(new(Id: 1), default)).Status);
        var wrongServer = Service(true, "http://other", principal, bridge, gate);
        Assert.Equal(PermissionStatus.VerificationUnavailable, (await wrongServer.VerifyAsync(new(Id: 1), default)).Status);
        Assert.Equal(0, principal.Calls); Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public async Task UnresolvedSsoTrusteeNeverInspectsAnEntry()
    {
        var principal = new Principals { Principal = null }; var bridge = new Bridge(); using var gate = new PermissionVerificationGate();
        Assert.Equal(PermissionStatus.Unknown, (await Service(true, "http://fixture", principal, bridge, gate).VerifyAsync(new(Id: 1), default)).Status);
        Assert.Equal(0, bridge.Calls);
    }

    [Fact]
    public async Task EveryUseRechecksRightsAndKeepsCurrentPrincipalAndRepository()
    {
        var principal = new Principals(); var bridge = new Bridge(); using var gate = new PermissionVerificationGate();
        var service = Service(true, "http://fixture", principal, bridge, gate);
        var first = await service.VerifyAsync(new(Id: 1), default);
        Assert.Equal(PermissionStatus.DocumentAccessDenied, first.Status);
        principal.Principal = new("user-B-generation", "user-B", null, "sid-B");
        bridge.Result = new(PermissionStatus.Unknown);
        Assert.Equal(PermissionStatus.Unknown, (await service.VerifyAsync(new(Id: 1), default)).Status);
        Assert.Equal(2, bridge.Calls);
        Assert.Equal("user-B-generation", bridge.Principal!.Scope); Assert.Equal("A", bridge.Repository);
    }

    [Fact]
    public async Task BudgetCancellationAndConnectionErrorsNeverBecomeMissingOrDenied()
    {
        var principal = new Principals(); var bridge = new Bridge { Error = new IOException("native secret") }; using var gate = new PermissionVerificationGate();
        // The process adapter handles IO failures; coordinator handles upstream connectivity separately.
        bridge.Error = new HttpRequestException("fixture");
        var service = Service(true, "http://fixture", principal, bridge, gate);
        Assert.Equal(PermissionStatus.VerificationUnavailable, (await service.VerifyAsync(new(Id: 1), default)).Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyAsync(new(Id: 1), cancelled.Token));
        bridge.Error = new UnauthorizedAccessException();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.VerifyAsync(new(Id: 1), default));
    }

    [Fact]
    public async Task RateLimitSurvivesNewSessionGenerationWithoutCachingDecisions()
    {
        var principal = new Principals(); var bridge = new Bridge(); using var gate = new PermissionVerificationGate();
        var service = Service(true, "http://fixture", principal, bridge, gate);
        for (var i = 0; i < 12; i++) await service.VerifyAsync(new(Id: 1), default);
        principal.Principal = principal.Principal! with { Scope = "new-generation" };
        Assert.Equal(PermissionStatus.Unknown, (await service.VerifyAsync(new(Id: 1), default)).Status);
        Assert.Equal(12, bridge.Calls);
    }

    [Fact]
    public async Task ContentRefreshChecksFailedTargetAndNeverContinuesToNextDocument()
    {
        var ingestion = new Ingestion(); var verifier = new Verifier();
        var error = await Assert.ThrowsAsync<PermissionVerificationException>(() =>
            ReportsChatService.RefreshContentAsync(ingestion, [5, 6], default, verifier));
        Assert.Equal(PermissionStatus.DocumentAccessDenied, error.Decision.Status);
        Assert.Equal(5, verifier.Target!.Id); Assert.Equal(1, ingestion.Calls);
    }

    private static PermissionVerificationService Service(bool enabled, string apiServer,
        Principals principals, Bridge bridge, PermissionVerificationGate gate)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["PermissionVerification:Enabled"] = enabled.ToString(),
            ["PermissionVerification:Repositories:A:ApiServerUrl"] = apiServer,
            ["PermissionVerification:Repositories:A:ServerName"] = "server",
            ["PermissionVerification:Repositories:A:RepositoryName"] = "native" }).Build();
        return new(config, new Repositories(), principals, bridge, gate, NullLogger<PermissionVerificationService>.Instance);
    }
    private sealed class Repositories : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) => Task.FromResult(new RepositoryDescriptor("A", "http://fixture", "A", "A"));
        public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) => [await GetActiveRepositoryAsync(ct)];
    }
    private sealed class Principals : IAuthenticatedLaserfichePrincipalResolver
    {
        public int Calls;
        public AuthenticatedPermissionPrincipal? Principal = new("user-A-generation", "user-A", null, "sid-A");
        public Task<AuthenticatedPermissionPrincipal?> ResolveAsync(PermissionRepositoryBinding b, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Principal); }
    }
    private sealed class Bridge : ILaserficheSdkBridge
    {
        public int Calls; public Exception? Error;
        public AuthenticatedPermissionPrincipal? Principal; public string? Repository;
        public PermissionDecision Result = new(PermissionStatus.DocumentAccessDenied, "document");
        public Task<PermissionDecision> VerifyAsync(PermissionTarget t, AuthenticatedPermissionPrincipal p, string r, PermissionRepositoryBinding b, CancellationToken ct)
        { Calls++; Principal = p; Repository = r; if (Error != null) throw Error; return Task.FromResult(Result); }
    }
    private sealed class Verifier : ILaserfichePermissionVerifier
    {
        public PermissionTarget? Target;
        public Task<PermissionDecision> VerifyAsync(PermissionTarget target, CancellationToken ct)
        { Target = target; return Task.FromResult(new PermissionDecision(PermissionStatus.DocumentAccessDenied, "document")); }
    }
    private sealed class Ingestion : ILaserficheDocumentIngestionService
    {
        public int Calls;
        public Task<DocumentIngestionResult> IngestMetadataAsync(int id, CancellationToken ct = default)
        { Calls++; throw new LaserficheException("hidden", 404); }
    }
}
