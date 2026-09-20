using System.Net;
using System.Net.Http.Headers;
using LaserficheReports.Infrastructure.Http;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class LaserficheRequestLoggingHandlerTests
{
    [Fact]
    public async Task BinaryResponse_PassesThroughWithoutChangingPngBytes()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];
        var content = new ByteArrayContent(pngBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = "page.png"
        };

        using var client = CreateClient(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });

        using var response = await client.GetAsync("https://lf.test/download/page.png");
        var actual = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(pngBytes, actual);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("page.png", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task JsonResponse_PreservesOriginalBytesAndHeadersAfterLogging()
    {
        byte[] jsonBytes = "{\"value\":\"ok\"}"u8.ToArray();
        var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        content.Headers.Add("X-Laserfiche-Test", "preserved");

        using var client = CreateClient(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });

        using var response = await client.GetAsync("https://lf.test/api");
        var actual = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(jsonBytes, actual);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("preserved", response.Content.Headers.GetValues("X-Laserfiche-Test").Single());
    }

    private static HttpClient CreateClient(HttpResponseMessage response)
    {
        var handler = new LaserficheRequestLoggingHandler(
            new StaticOptionsMonitor(new LaserficheOptions
            {
                ServerUrl = "https://lf.test",
                ApiBasePath = "/LFRepositoryAPI"
            }),
            NullLogger<LaserficheRequestLoggingHandler>.Instance)
        {
            InnerHandler = new StubHandler(response)
        };

        return new HttpClient(handler);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class StaticOptionsMonitor(LaserficheOptions value)
        : IOptionsMonitor<LaserficheOptions>
    {
        public LaserficheOptions CurrentValue => value;
        public LaserficheOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LaserficheOptions, string?> listener) => null;
    }
}
