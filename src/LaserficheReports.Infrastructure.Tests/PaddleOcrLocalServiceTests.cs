using System.Net;
using System.Text;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Services;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class PaddleOcrLocalServiceTests
{
    [Fact]
    public async Task TryExtractTextAsync_PostsImageAndReturnsVerifiedArabicText()
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
                    "{\"text\":\"عنوان\\nالبيان القيمة\",\"engine\":\"PaddleOCR\",\"model\":\"arabic_PP-OCRv5_mobile_rec\",\"imageSha256\":\"0f4636c78f65d3639ece5a064b5ae753e3408614a14fb18ab4d7540d2c248543\",\"lineCount\":2,\"meanConfidence\":0.91}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var service = CreateService(handler);

        await using var image = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        var result = await service.TryExtractTextAsync(image);

        Assert.Equal("عنوان\nالبيان القيمة", result);
        Assert.Equal("application/octet-stream", requestMediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, requestBody);
    }

    [Fact]
    public void ValidateResponseIdentity_RejectsOldGenerativeWorker()
    {
        var exception = Assert.Throws<LocalOcrException>(() =>
            PaddleOcrLocalService.ValidateResponseIdentity(
                [1, 2, 3],
                new PaddleOcrLocalService.PaddleOcrResponse(
                    "invented", "PaddleOCR-VL", "v1.6", null, null, null, null)));

        Assert.Contains("hallucinate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateResponseIdentity_AcceptsNonGenerativeStructureWorker()
    {
        var image = new byte[] { 1, 2, 3 };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant();

        PaddleOcrLocalService.ValidateResponseIdentity(
            image,
            new PaddleOcrLocalService.PaddleOcrResponse(
                "نص", "PP-StructureV3", "arabic_PP-OCRv5_mobile_rec", hash, 1, 0.9, 10));
    }

    [Fact]
    public void ValidateResponseIdentity_RejectsDifferentImageHash()
    {
        var exception = Assert.Throws<LocalOcrException>(() =>
            PaddleOcrLocalService.ValidateResponseIdentity(
                [1, 2, 3],
                new PaddleOcrLocalService.PaddleOcrResponse(
                    "text", "PaddleOCR", "arabic_PP-OCRv5_mobile_rec", "wrong", 1, 0.9, 10)));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
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
