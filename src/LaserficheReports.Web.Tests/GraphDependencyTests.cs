using System.Net;
using System.Text;
using LaserficheReports.Web;
using Xunit;

namespace LaserficheReports.Web.Tests;

public sealed class GraphDependencyTests
{
    [Fact]
    public async Task GraphCallsCarryTheCurrentRequestIdInsteadOfAStaleHeader()
    {
        string? actual = null;
        var endpoint = new Factory(request =>
        {
            actual = Assert.Single(request.Headers.GetValues("X-Request-ID"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var handler = new GraphCorrelationHandler(new Microsoft.AspNetCore.Http.HttpContextAccessor
        { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { TraceIdentifier = "request:17" } })
        { InnerHandler = endpoint };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("X-Request-ID", "old");
        using var response = await client.GetAsync("http://graph.test/health");
        Assert.Equal("request:17", actual);
    }

    [Fact]
    public async Task UnlimitedModelTimeoutIsAcceptedByTheHealthHandshake()
    {
        var factory = new Factory(request => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(request.RequestUri!.AbsolutePath == "/health"
            ? "{\"routingVersion\":\"schema-agent-v5\",\"modelTimeoutSeconds\":0}"
            : "{\"reports\":[{\"operation\":\"search\",\"resultType\":\"documents\",\"requiresFilter\":false}]}", Encoding.UTF8, "application/json") });
        var result = await new QuestionRouter(factory).RouteAsync("اعرض الوثائق", new { }, default);
        Assert.Single(result.Reports);
        Assert.Equal("search", result.Reports[0].Operation);
    }

    [Theory]
    [InlineData("ollama_unavailable")]
    [InlineData("model_not_found")]
    [InlineData("local_model_timeout")]
    [InlineData("local_model_invalid_output")]
    public async Task PlanningPreservesDependencyCauseWithoutAFixedAnswerFallback(string code)
    {
        var factory = new Factory(request => request.RequestUri!.AbsolutePath == "/health"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"routingVersion\":\"schema-agent-v5\",\"modelTimeoutSeconds\":600}", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("{\"error\":\"" + code + "\"}", Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<GraphServiceException>(() => new QuestionRouter(factory)
            .RouteAsync("آخر تعديل وآخر إنشاء", new { fields = new[] { "إجراء الوثيقة" } }, default));
        Assert.Equal(code, error.Code);
        Assert.Equal("planning", error.Stage);
        Assert.Equal(new[] { "/health", "/route" }, factory.Paths);
    }

    [Theory]
    [InlineData("{\"status\":\"ready\"}")]
    [InlineData("{\"routingVersion\":\"schema-agent-v4\",\"modelTimeoutSeconds\":600}")]
    [InlineData("{\"routingVersion\":\"schema-agent-v5\"}")]
    [InlineData("{\"routingVersion\":\"schema-agent-v5\",\"modelTimeoutSeconds\":40}")]
    public async Task OldRunningGraphIsDetectedBeforePostingAnIncompatiblePlan(string health)
    {
        var factory = new Factory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(health, Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<GraphServiceException>(() => new QuestionRouter(factory)
            .RouteAsync("اعطني الوثائق تحت الإجراء", new { }, default));
        Assert.Equal("graph_protocol_mismatch", error.Code);
        Assert.Single(factory.Paths);
    }

    [Fact]
    public async Task StoppedGraphIsNotReportedAsAnEmptyRepository()
    {
        var factory = new Factory(_ => throw new HttpRequestException("connection refused"));
        var error = await Assert.ThrowsAsync<GraphServiceException>(() => new QuestionRouter(factory)
            .RouteAsync("عدد الوثائق", new { }, default));
        Assert.Equal("graph_unavailable", error.Code);
    }

    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Paths { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("http://graph.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }
}
