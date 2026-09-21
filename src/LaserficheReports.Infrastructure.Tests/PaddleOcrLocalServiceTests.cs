using System.Net;
using System.Text;
using LaserficheReports.Infrastructure.Services;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class PaddleOcrLocalServiceTests
{
    [Fact]
    public async Task TryExtractTextAsync_PostsImageAndReturnsLayoutMarkdown()
    {
        byte[]? requestBody = null;
        string? requestMediaType = null;
        var handler = new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsByteArrayAsync();
            requestMediaType = request.Content.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"text\":\"# عنوان\\n\\n| البيان | القيمة |\",\"engine\":\"PaddleOCR-VL\",\"model\":\"v1.6\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var service = CreateService(handler);

        await using var image = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        var result = await service.TryExtractTextAsync(image);

        Assert.Equal("# عنوان\n\n| البيان | القيمة |", result);
        Assert.Equal("application/octet-stream", requestMediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, requestBody);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765")]
    [InlineData("http://localhost:8765")]
    [InlineData("http://[::1]:8765")]
    public void ValidateLoopbackBaseUrl_AcceptsOnlyLocalAddresses(string value)
    {
        var result = PaddleOcrLocalService.ValidateLoopbackBaseUrl(value);

        Assert.True(result.IsLoopback);
    }

    [Theory]
    [InlineData("https://ocr.example.com")]
    [InlineData("http://192.168.1.20:8765")]
    public void ValidateLoopbackBaseUrl_RejectsRemoteAddresses(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => PaddleOcrLocalService.ValidateLoopbackBaseUrl(value));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("  Arabic text\r\nEnglish text  ", "Arabic text\nEnglish text")]
    [InlineData("\u200EInv#\u200F 3521\n\uFFFD العربية", "Inv# 3521\n العربية")]
    [InlineData("# عنوان\n\n\n| البيان | القيمة |", "# عنوان\n\n| البيان | القيمة |")]
    [InlineData("   ", "")]
    public void NormalizeText_PreservesMarkdownAndRemovesInvalidControls(string input, string expected)
    {
        Assert.Equal(expected, PaddleOcrLocalService.NormalizeText(input));
    }

    private static PaddleOcrLocalService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8765/")
        };
        return new PaddleOcrLocalService(
            new StubHttpClientFactory(client),
            Microsoft.Extensions.Options.Options.Create(new PaddleOcrOptions()),
            NullLogger<PaddleOcrLocalService>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request);
    }
}
