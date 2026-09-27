using System.Net;
using System.Text;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class OllamaOcrTextCorrectionServiceTests
{
    [Fact]
    public async Task CorrectAsync_RejectsUnverifiedArabicCorrection()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"response\":\"{\\\"corrected_text\\\":\\\"قرار مجلس الإدارة رقم 27 بتاريخ 1448/03/27\\\"}\"}",
                Encoding.UTF8,
                "application/json")
        }));
        var service = CreateService(handler);

        var result = await service.CorrectAsync("قرار الجلس الإدارة رقم 27 بتاريخ 1448/03/27");

        Assert.False(result.WasCorrected);
        Assert.Equal("قرار الجلس الإدارة رقم 27 بتاريخ 1448/03/27", result.Text);
        Assert.Equal("visual-verification-required", result.Diagnostic);
        Assert.Equal("qwen2.5:7b", result.Model);
    }

    [Fact]
    public async Task CorrectAsync_RetainsOriginalWhenLocalModelIsUnavailable()
    {
        var handler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("model not found")
            }));
        var service = CreateService(handler);
        const string original = "لائحة المنطقة الاقتصادية الخاصة بجازان";

        var result = await service.CorrectAsync(original);

        Assert.False(result.WasCorrected);
        Assert.Equal(original, result.Text);
        Assert.Equal("http-404", result.Diagnostic);
    }

    [Fact]
    public void ValidateCorrection_RejectsChangedLegalNumberOrDate()
    {
        var error = OllamaOcrTextCorrectionService.ValidateCorrection(
            "قرار رقم 27 بتاريخ 1448/03/27",
            "قرار رقم 28 بتاريخ 1448/03/27");

        Assert.Equal("numbers-or-dates-changed", error);
    }

    [Fact]
    public void ValidateCorrection_RequiresVisualEvidenceForLetterChanges()
    {
        var error = OllamaOcrTextCorrectionService.ValidateCorrection(
            "الجهة العنية والقرار رقم 27",
            "الجهة المعنية والقرار رقم 27");

        Assert.Equal("visual-verification-required", error);
    }

    [Theory]
    [InlineData("محمد رقم ١٢", "محمود رقم ١٢", "visual-verification-required")]
    [InlineData("رقم ١٢", "رقم 12", "numbers-or-dates-changed")]
    [InlineData("أ 12 ب 34", "أ 34 ب 12", "numbers-or-dates-changed")]
    public void ValidateCorrection_ProtectsNamesDigitShapesAndOrder(
        string original, string candidate, string expected)
    {
        Assert.Equal(expected, OllamaOcrTextCorrectionService.ValidateCorrection(original, candidate));
    }

    private static OllamaOcrTextCorrectionService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        return new OllamaOcrTextCorrectionService(
            new StubHttpClientFactory(client),
            Microsoft.Extensions.Options.Options.Create(new LocalAiOptions
            {
                OcrCorrectionEnabled = true,
                OcrCorrectionModel = "qwen2.5:7b"
            }),
            NullLogger<OllamaOcrTextCorrectionService>.Instance);
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
