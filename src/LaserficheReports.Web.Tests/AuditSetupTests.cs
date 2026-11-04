using System.Text.Json.Nodes;
using LaserficheReports.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class AuditSetupTests
{
    private static AuditSetupRequest Setup() => new(true, "https://audit.example/AuditTrail", new() {
        DataSource = "Repo(Server)", ReportId = "report", ExportDirectory = Path.GetTempPath(),
        ScopeDescription = "Saved report scope", Columns = new() {
            ["eventTime"] = "Time", ["eventType"] = "Event", ["userName"] = "User" } });
    [Fact]
    public async Task AuditSavePreservesOtherMachineConnectionAndRepositoryBindings()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"Laserfiche":{"ServerUrl":"https://second-machine","ApiVersion":"v2"},
                 "ReportsGraph":{"BaseUrl":"http://127.0.0.1:5050"},
                 "LocalAI":{"ChatModel":"installed-model"},
                 "AuditTrail":{"MaxRows":10,"Repositories":{"repo":{"ReportId":"old"},"Other":{"ReportId":"keep"}}}}
                """);
            await AuditTrailConfiguration.SaveFileAsync(path, "Repo", Setup(), default);
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.Equal("https://second-machine", root["Laserfiche"]!["ServerUrl"]!.GetValue<string>());
            Assert.Equal("http://127.0.0.1:5050", root["ReportsGraph"]!["BaseUrl"]!.GetValue<string>());
            Assert.Equal("installed-model", root["LocalAI"]!["ChatModel"]!.GetValue<string>());
            Assert.Equal(3600, root["AuditTrail"]!["TimeoutSeconds"]!.GetValue<int>());
            Assert.Equal(10, root["AuditTrail"]!["MaxRows"]!.GetValue<int>());
            Assert.Equal("keep", root["AuditTrail"]!["Repositories"]!["Other"]!["ReportId"]!.GetValue<string>());
            Assert.Equal("report", root["AuditTrail"]!["Repositories"]!["repo"]!["ReportId"]!.GetValue<string>());
            Assert.Null(root["AuditTrail"]!["Repositories"]!["Repo"]);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task CorruptConfigurationAndInvalidMappingsAreNeverOverwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "invalid-json");
            await Assert.ThrowsAsync<ArgumentException>(() => AuditTrailConfiguration.SaveFileAsync(path, "Repo", Setup(), default));
            Assert.Equal("invalid-json", await File.ReadAllTextAsync(path));
            var invalid = Setup();invalid.Report.Columns.Remove("eventTime");
            Assert.Throws<ArgumentException>(() => AuditTrailConfiguration.Validate(invalid));
            Assert.Throws<ArgumentException>(() => AuditTrailConfiguration.Validate(Setup() with { BaseUrl = "http://remote/AuditTrail" }));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void WindowsCredentialsAreEncryptedAndIsolatedFromOtherBrowserSessions()
    {
        var first = new DefaultHttpContext { Session = new MemorySession() };
        var second = new DefaultHttpContext { Session = new MemorySession() };
        var accessor = new HttpContextAccessor { HttpContext = first };
        var store = new AuditSessionCredentials(accessor, new EphemeralDataProtectionProvider());
        store.Store("DOMAIN\\audit", "audit-password");
        Assert.DoesNotContain("audit-password", first.Session.GetString(AuditSessionCredentials.SessionKey));
        Assert.Equal("DOMAIN\\audit", store.Read()!.Username);
        accessor.HttpContext = second;Assert.Null(store.Read());
        accessor.HttpContext = first;first.Session.Remove(AuditSessionCredentials.SessionKey);Assert.Null(store.Read());
        Assert.Throws<ArgumentException>(() => store.Store("repositoryAdmin", "password"));
    }
    [Theory]
    [InlineData("/api/reports/chat")]
    [InlineData("/api/reports/audit")]
    [InlineData("/api/reports/planning-catalog")]
    [InlineData("/api/audit-trail/test")]
    public void ReportCatalogAndExportUseTheFullRequestBudget(string path) => Assert.True(ReportTimeouts.IsReportRequest(path));
    internal sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public bool IsAvailable => true;public string Id => "test";public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
    }
}
