using System.Net;
using System.Text;
using LaserficheReports.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Web.Tests;

public class LiveAiClientTests
{
    [Fact]
    public async Task DiscoversCompletionModelAndSkipsEmbeddings()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => "{\"models\":[{\"name\":\"embed-only\"},{\"name\":\"chat-model\"}]}",
            "/api/show" => "{\"capabilities\":[\"completion\"]}",
            _ => throw new Exception("Unexpected request")
        });
        var client = Create(handler);
        Assert.Equal("chat-model", await client.ResolveModelAsync(default));
        Assert.Equal("chat-model", await client.ResolveModelAsync(default));
        Assert.Equal(2, handler.Calls); // Reuse selection within this request.
    }
    [Fact]
    public async Task MissingConfiguredModelDoesNotSilentlySelectAnother()
    {
        var handler = new Handler(_ => "{\"models\":[{\"name\":\"different-model\"}]}");
        var client = Create(handler, new() { ["LocalAI:ChatModel"] = "missing" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResolveModelAsync(default));
    }
    [Fact]
    public async Task EmbeddingOnlyServerIsNotReportedAsReady()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/tags"
            ? "{\"models\":[{\"name\":\"encoder\"}]}" : "{\"capabilities\":[\"embedding\"]}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(handler).ResolveModelAsync(default));
    }
    [Fact]
    public async Task OpenAiCompatibleModelDiscoveryUsesActualModelId()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? "{\"data\":[{\"id\":\"text-embedding\"},{\"id\":\"loaded-chat\"}]}" : throw new Exception("Wrong endpoint"));
        Assert.Equal("loaded-chat", await Create(handler, new() { ["LocalAI:Provider"] = "LMStudio" }).ResolveModelAsync(default));
    }
    [Fact]
    public async Task EmptyModelAnswerIsReportedAsFailure()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => "{\"models\":[{\"name\":\"chat-model\"}]}",
            "/api/show" => "{\"capabilities\":[\"completion\"]}",
            "/api/chat" => "{\"message\":{\"content\":\"\"},\"done\":true}\n",
            _ => throw new Exception("Unexpected request")
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var text in Create(handler).AnalyzeAsync("كم وثيقة؟", "العدد 17", default)) { }
        });
    }
    [Fact]
    public void DocumentContentRequestRemainsExplicitlyUnsupported()
    {
        Assert.Equal("unsupported", QuestionRouter.TryRoute("لخص أهم النقاط في الوثيقة 618 كتقرير", new(2026,10,6))!.Intent);
    }
    private static LiveAiClient Create(Handler handler, Dictionary<string,string?>? settings = null) =>
        new(new Factory(handler), new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build(), NullLogger<LiveAiClient>.Instance);
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler:false) { BaseAddress = new("http://fixture/") };
    }
    private sealed class Handler(Func<HttpRequestMessage,string> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request), Encoding.UTF8, "application/json") });
        }
    }
}
