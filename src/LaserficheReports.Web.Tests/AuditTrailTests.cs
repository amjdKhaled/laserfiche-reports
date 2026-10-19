using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class AuditTrailTests
{
    private static AuditTrailReportOptions Mapping() => new() { Columns = new() {
        ["eventTime"] = "When", ["eventType"] = "Event", ["userName"] = "User",
        ["entryId"] = "ID", ["entryName"] = "Name", ["outcome"] = "Result" } };
    private const string Csv = "When,Event,User,ID,Name,Result\n2026-10-18T21:00:00Z,Recycle,ACME\\alice,619,\"Name, with comma\",Success\n2026-10-19T21:00:00Z,Restore,ACME\\bob,619,\"Two\nlines\",Success\n";

    [Fact]
    public void CsvPreservesRepeatedDocumentEventsAndQuotedFields()
    {
        var rows = AuditCsv.Parse(Csv, Mapping(), 10);
        Assert.Equal(2, rows.Count);
        Assert.Equal(619, rows[0].EntryId);
        Assert.Equal(619, rows[1].EntryId);
        Assert.Equal("Name, with comma", rows[0].EntryName);
        Assert.Contains("lines", rows[1].EntryName);
        var tsv = "When\tEvent\tUser\tID\tName\tResult\n2026-10-19T00:00:00Z\tLogin\tACME\\alice\t\t\tFailed\n";
        Assert.Null(Assert.Single(AuditCsv.Parse(tsv, Mapping(), 10)).EntryId);
    }
    [Fact]
    public void MalformedMissingColumnsDatesAndTruncationAreNeverZeroCounts()
    {
        Assert.Throws<AuditTrailException>(() => AuditCsv.Parse(Csv, Mapping(), 1));
        Assert.Throws<AuditTrailException>(() => AuditCsv.Parse("", Mapping(), 10));
        Assert.Throws<AuditTrailException>(() => AuditCsv.Parse(Csv.Replace("When,", "Other,"), Mapping(), 10));
        Assert.Throws<AuditTrailException>(() => AuditCsv.Parse(Csv.Replace("2026-10-18T21:00:00Z", "not-a-date"), Mapping(), 10));
        Assert.Throws<AuditTrailException>(() => AuditCsv.Parse("When,Event,User,ID,Name,Result\n\"unfinished", Mapping(), 10));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportUsesEncodedRawProtocolFreshFilenameAndCurrentSessionWindowsIdentity(bool pascalCase)
    {
        using var fixture = new Fixture();
        fixture.PascalCase = pascalCase;
        var source = fixture.Source();
        var first = await source.ReadAsync(default);
        Assert.Equal(2, first.Events.Count);
        Assert.Same(first, await source.ReadAsync(default));
        Assert.Equal(1, fixture.Exports);
        Assert.Equal("alice", fixture.Identity!.UserName);
        Assert.Equal("ACME", fixture.Identity.Domain);
        Assert.Equal(2, fixture.StatusChecks);
        Assert.All(fixture.Requests, request => Assert.False(request.Headers.Contains("Authorization")));
        Assert.Contains("dataSourceId=Repo%28server%29", fixture.Requests[0].RequestUri!.OriginalString);
        Assert.Contains("report%2Fid/raw", fixture.Requests[0].RequestUri!.OriginalString);
        Assert.Contains("tz=UTC", fixture.Requests[0].RequestUri!.OriginalString);
        await fixture.Source().ReadAsync(default);
        Assert.Equal(2, fixture.Exports);
        Assert.Equal(2, Directory.GetFiles(fixture.Root).Length); // no file deletion, overwrite or reuse
    }
    [Fact]
    public async Task CountsUseEventsNotDistinctIdsAndRiyadhEndIsExclusive()
    {
        using var fixture = new Fixture();
        var result = await AuditTrailReport.CreateAsync(fixture.Source(), "Repo",
            new QueryPlan("audit", CountOnly: true, ResultType: "count", Audit: new()), default);
        Assert.Contains("| 2 |", result.Answer);
        Assert.Empty(result.RelatedEntryIds);
        var day = await AuditTrailReport.CreateAsync(fixture.Source(), "Repo",
            new QueryPlan("audit", Audit: new(From: "2026-10-19", To: "2026-10-20")), default);
        Assert.Contains("Recycle", day.Answer);
        Assert.DoesNotContain("Restore", day.Answer);
        Assert.Contains("2026-10-19 00:00:00", day.Answer);
        var grouped = await AuditTrailReport.CreateAsync(fixture.Source(), "Repo",
            new QueryPlan("audit", ResultType: "statistics", Audit: new(GroupBy: "userName")), default);
        Assert.Contains("| " + ReportSupport.Cell("ACME\\alice") + " | 1 |", grouped.Answer);
        Assert.Contains("| " + ReportSupport.Cell("ACME\\bob") + " | 1 |", grouped.Answer);
    }
    [Fact]
    public async Task DeniedFailedAndMissingExportsDoNotReturnEmptySuccess()
    {
        using var fixture = new Fixture();
        fixture.ResponseCode = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<AuditTrailException>(() => fixture.Source().ReadAsync(default));
        fixture.ResponseCode = HttpStatusCode.OK;
        fixture.FailExport = true;
        await Assert.ThrowsAsync<AuditTrailException>(() => fixture.Source().ReadAsync(default));
        Assert.Contains(fixture.Requests, request => request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath.EndsWith("/cancel"));
        fixture.FailExport = false;
        fixture.WriteFile = false;
        await Assert.ThrowsAsync<AuditTrailException>(() => fixture.Source().ReadAsync(default));
    }
    [Fact]
    public async Task NoFallbackToServiceAccountAndNoCrossRepositoryDataSource()
    {
        using var fixture = new Fixture();
        fixture.Credentials = new("repositoryAdmin", "secret");
        await Assert.ThrowsAsync<AuditTrailException>(() => fixture.Source().ReadAsync(default));
        Assert.Empty(fixture.Requests);
        fixture.Credentials = new("ACME\\alice", "secret");
        var source = fixture.Source();
        await source.ReadAsync(default);
        fixture.RepositoryId = "OtherRepo";
        await Assert.ThrowsAsync<AuditTrailException>(() => source.ReadAsync(default));
        await Assert.ThrowsAsync<AuditTrailException>(() => fixture.Source().ReadAsync(default));
        Assert.Equal(1, fixture.Exports);
    }
    [Fact]
    public void UnsupportedSelectorsAndMixedOutputFailValidation()
    {
        Assert.Throws<ArgumentException>(() => new QueryPlan("search", Audit: new()).ValidateIntent());
        Assert.Throws<ArgumentException>(() => new QueryPlan("audit", Name: "ignored", Audit: new()).ValidateIntent());
        Assert.Throws<ArgumentException>(() => new QueryPlan("audit", CountOnly: true, Audit: new(GroupBy: "userName")).ValidateIntent());
        Assert.Throws<ArgumentException>(() => new QueryPlan("audit", Audit: new(From: "2026-01-01", FromRelative: new())).ValidateIntent());
    }

    private sealed class Fixture : IDisposable, IRepositoryContext, ISessionCredentialStore
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "reports-audit-test-" + Guid.NewGuid().ToString("N"));
        public string RepositoryId = "Repo";
        public LaserficheCredential? Credentials = new("ACME\\alice", "secret");
        public NetworkCredential? Identity;
        public int Exports, StatusChecks;
        public bool FailExport, PascalCase, WriteFile = true;
        public HttpStatusCode ResponseCode = HttpStatusCode.OK;
        public List<HttpRequestMessage> Requests = [];
        public Fixture() => Directory.CreateDirectory(Root);
        public AuditTrailSource Source()
        {
            var config = new Dictionary<string, string?> {
                ["AuditTrail:Enabled"] = "true", ["AuditTrail:BaseUrl"] = "https://audit.example/AuditTrail",
                ["AuditTrail:PollMilliseconds"] = "10", ["AuditTrail:Repositories:Repo:ReportId"] = "report/id",
                ["AuditTrail:Repositories:Repo:DataSource"] = "Repo(server)",
                ["AuditTrail:Repositories:Repo:ExportDirectory"] = Root,
                ["AuditTrail:Repositories:Repo:ScopeDescription"] = "Saved report test window" };
            foreach (var column in Mapping().Columns) config["AuditTrail:Repositories:Repo:Columns:" + column.Key] = column.Value;
            var source = new AuditTrailSource(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), this, this, NullLogger<AuditTrailSource>.Instance);
            source.HandlerFactory = credentials => { Identity = credentials; return new Handler(this); };
            return source;
        }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            private string? filename;
            private int statusChecks;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                fixture.Requests.Add(request);
                if (fixture.ResponseCode != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(fixture.ResponseCode));
                object body;
                if (request.Method == HttpMethod.Post)
                {
                    fixture.Exports++;
                    filename = QueryHelpers.ParseQuery(request.RequestUri!.Query)["destFile"].ToString();
                    Assert.Equal(Path.GetFileName(filename), filename);
                    body = "job-token";
                }
                else if (request.Method == HttpMethod.Delete) body = new { };
                else
                {
                    statusChecks++; fixture.StatusChecks++;
                    var state = fixture.FailExport ? 5 : statusChecks == 1 ? 1 : 2;
                    if (state == 2 && fixture.WriteFile) File.WriteAllText(Path.Combine(fixture.Root, filename!), Csv);
                    body = new { State = state, Completion = state == 2 ? 100 : 50 };
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType(), options: new JsonSerializerOptions { PropertyNamingPolicy = fixture.PascalCase ? null : JsonNamingPolicy.CamelCase }) });
            }
        }
        public void Dispose() => Directory.Delete(Root, true);
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken ct = default) => Task.FromResult(new RepositoryDescriptor("key", "https://lf.example", RepositoryId, RepositoryId));
        public async Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken ct = default) => [await GetActiveRepositoryAsync(ct)];
        public Task<LaserficheCredential?> TryGetAsync(CancellationToken ct = default) => Task.FromResult(Credentials);
        public Task StoreAsync(string user, string password, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
