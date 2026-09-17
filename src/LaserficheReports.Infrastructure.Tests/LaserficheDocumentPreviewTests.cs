using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LaserficheReports.Application.DTOs;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Adapters;
using LaserficheReports.Infrastructure.Options;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class LaserficheDocumentPreviewTests
{
    [Fact]
    public async Task ElectronicDocument_V2UsesDocumentedExportFlow()
    {
        var export = Json("{\"value\":\"https://lf.test/download/document.pdf\"}");
        var pdf = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46])
        };
        pdf.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        pdf.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "document.pdf" };

        var handler = new QueueHandler(export, pdf);
        var service = CreateService(handler);

        using var result = await service.StreamEdocAsync(42);

        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/Entries/42/Export", handler.Requests[0].Url);
        Assert.Equal("https://lf.test/download/document.pdf", handler.Requests[1].Url);
    }

    [Fact]
    public async Task TiffPage_IsExportedAsBrowserSafePngOnV2()
    {
        var tiff = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x49, 0x49, 0x2A, 0x00])
        };
        tiff.Content.Headers.ContentType = new MediaTypeHeaderValue("image/tiff");
        tiff.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileName = "page.tif" };

        var export = Json("{\"value\":\"https://lf.test/download/page.png\"}");
        var png = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47])
        };
        png.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var handler = new QueueHandler(tiff, export, png);
        var service = CreateService(handler);

        using var result = await service.GetPageImageAsync(42, 1);

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("/Entries/42/Document/Pages/1/Image", handler.Requests[0].Url);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.EndsWith("/Entries/42/Export?pageRange=1", handler.Requests[1].Url);
        Assert.Equal("https://lf.test/download/page.png", handler.Requests[2].Url);
    }

    [Fact]
    public async Task MissingV1PageImage_FallsBackToDocumentEdoc()
    {
        var missingPage = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("page image route is unavailable")
        };
        var pdf = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46])
        };
        pdf.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        pdf.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = "document.pdf"
        };

        var handler = new QueueHandler(missingPage, pdf);
        var service = CreateService(handler, "v1");

        using var result = await service.GetPageImageAsync(42, 1);

        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/Entries/42/pages/1/image", handler.Requests[0].Url);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.EndsWith(
            "/Entries/42/Laserfiche.Repository.Document/edoc",
            handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("application/octet-stream", "scan.pdf", "application/pdf")]
    [InlineData(null, "scan.jpeg", "image/jpeg")]
    [InlineData("image/png", null, "image/png")]
    public void ContentType_IsRecoveredFromFilenameWhenHeaderIsGeneric(
        string? header,
        string? fileName,
        string expected) =>
        Assert.Equal(expected, LaserficheDocumentService.NormalizeContentType(header, fileName));

    [Theory]
    [InlineData("{\"value\":\"https://lf.test/file\"}")]
    [InlineData("\"https://lf.test/file\"")]
    public void ExportLinkParser_AcceptsSupportedResponses(string body) =>
        Assert.Equal("https://lf.test/file", LaserficheDocumentService.ParseExportDownloadLink(body));

    private static LaserficheDocumentService CreateService(QueueHandler handler, string apiVersion = "v2")
    {
        var options = new LaserficheOptions
        {
            ServerUrl = "https://lf.test",
            ApiBasePath = "/LFRepositoryAPI",
            ApiVersion = apiVersion
        };
        var factory = new ClientFactory(handler);
        var adapter = new LaserficheApiAdapter(new StaticOptionsMonitor(options));
        return new LaserficheDocumentService(
            factory,
            new RepositoryContext(),
            null!,
            adapter,
            NullLogger<LaserficheDocumentService>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<(HttpMethod Method, string Url)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RepositoryContext : IRepositoryContext
    {
        private static readonly RepositoryDescriptor Repository =
            new("test", "https://lf.test", "Documents", "Documents");

        public Task<RepositoryDescriptor> GetActiveRepositoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Repository);

        public Task<IReadOnlyList<RepositoryDescriptor>> GetAllRepositoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RepositoryDescriptor>>([Repository]);
    }

    private sealed class StaticOptionsMonitor(LaserficheOptions value) : IOptionsMonitor<LaserficheOptions>
    {
        public LaserficheOptions CurrentValue => value;
        public LaserficheOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LaserficheOptions, string?> listener) => null;
    }
}
