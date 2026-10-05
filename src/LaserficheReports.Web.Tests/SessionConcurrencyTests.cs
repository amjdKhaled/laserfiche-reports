using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace LaserficheReports.Web.Tests;

public sealed class SessionConcurrencyTests
{
    [Fact]
    public async Task LongReportDoesNotBlockStatusOrOverwriteANewerSession()
    {
        var reportStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReport = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new ReportsSessionMiddleware(async http =>
        {
            if (http.Request.Path == "/api/reports/chat")
            {
                reportStarted.SetResult(true);
                await releaseReport.Task;
            }
            else statusStarted.SetResult(true);
        });
        var report = Context("/api/reports/chat", "concurrent-report");
        var reportTask = middleware.InvokeAsync(report, new Repository());
        await reportStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await middleware.InvokeAsync(Context("/api/app/status", "concurrent-report"), new Repository())
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(statusStarted.Task.IsCompleted);
            Assert.Equal(0, ((Session)report.Session).Commits);
        }
        finally { releaseReport.TrySetResult(true); await reportTask; }
        Assert.Equal(0, ((Session)report.Session).Commits);
    }

    [Fact]
    public async Task LoginChangesRemainSerializedAndCommittedBeforeNextSnapshot()
    {
        var loginStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLogin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new ReportsSessionMiddleware(async http =>
        {
            if (http.Request.Path == "/api/session/login")
            {
                loginStarted.SetResult(true);
                await releaseLogin.Task;
                http.Session.SetString("ReportsGeneration", "new");
            }
            else statusStarted.SetResult(true);
        });
        var login = Context("/api/session/login", "serialized-login");
        var loginTask = middleware.InvokeAsync(login, new Repository());
        await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var statusTask = middleware.InvokeAsync(Context("/api/session/status", "serialized-login"), new Repository());
        Assert.False(statusStarted.Task.IsCompleted);
        releaseLogin.SetResult(true);
        await Task.WhenAll(loginTask, statusTask).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, ((Session)login.Session).Commits);
        Assert.True(statusStarted.Task.IsCompleted);
    }

    private static DefaultHttpContext Context(string path, string cookie)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.Headers["Cookie"] = ".LaserficheReports.Session=" + cookie;
        http.Features.Set<ISessionFeature>(new SessionFeature { Session = new Session() });
        return http;
    }
    private sealed class SessionFeature : ISessionFeature { public ISession Session { get; set; } = null!; }
    private sealed class Session : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public int Commits { get; private set; }
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) { Commits++; return Task.CompletedTask; }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[]? value) => values.TryGetValue(key, out value);
    }
    private sealed class Repository : IRepositoryContext
    {
        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryDescriptor("repo", "https://localhost", "repo", "repo"));
        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
