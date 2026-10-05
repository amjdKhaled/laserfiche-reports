using System.Net;
using LaserficheReports.Infrastructure.Http;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;
public class TransientReadHandlerTests
{
    [Fact]
    public async Task TransientReadRetriesThenSucceeds()
    {
        var stub = new Stub(n => n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = Client(stub);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://"+Guid.NewGuid()+".test/data")).StatusCode);
        Assert.Equal(2, stub.Calls);
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AuthenticationAndValidationErrorsAreNotRetried(HttpStatusCode status)
    {
        var stub = new Stub(_ => status); using var client = Client(stub);
        Assert.Equal(status, (await client.GetAsync("https://"+Guid.NewGuid()+".test/data")).StatusCode);
        Assert.Equal(1, stub.Calls);
    }
    [Fact]
    public async Task PostsAreNeverReplayedOnTransientFailure()
    {
        var stub = new Stub(_ => HttpStatusCode.ServiceUnavailable); using var client = Client(stub);
        await client.PostAsync("https://"+Guid.NewGuid()+".test/search", new StringContent("payload"));
        Assert.Equal(1, stub.Calls);
    }
    [Fact]
    public async Task CircuitFailsFastAfterRepeatedOutages()
    {
        var stub = new Stub(_ => HttpStatusCode.ServiceUnavailable); using var client = Client(stub);
        var url = "https://"+Guid.NewGuid()+".test/data";
        for (var i=0;i<3;i++) await client.GetAsync(url);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
        Assert.Equal(6,stub.Calls);
    }
    private static HttpClient Client(Stub stub) => new(new TransientReadHandler { InnerHandler = stub });
    private sealed class Stub(Func<int,HttpStatusCode> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(response(++Calls)) { Content = new StringContent("{}") });
    }
}
