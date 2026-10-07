using System.Net;
using LaserficheReports.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class RepositoryDefinitionReaderTests
{
    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task TransientGatewayFailure_RetriesSafeReadOnce(int status)
    {
        using var handler = new SequenceHandler(status, 200);
        using var client = new HttpClient(handler);
        using var response = await RepositoryDefinitionReader.GetAsync(client, "https://lf.test/FieldDefinitions", NullLogger.Instance, default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(400, 1)]
    [InlineData(504, 2)]
    public async Task PersistentFailure_ReturnsActualErrorWithoutUnboundedRetries(int status, int calls)
    {
        using var handler = new SequenceHandler(status, status);
        using var client = new HttpClient(handler);
        using var response = await RepositoryDefinitionReader.GetAsync(client, "https://lf.test/TemplateDefinitions", NullLogger.Instance, default);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringRetryDelay_StopsWithoutAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new SequenceHandler(504, 200) { AfterResponse = () => cancellation.Cancel() };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepositoryDefinitionReader.GetAsync(client, "https://lf.test/FieldDefinitions", NullLogger.Instance, cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class SequenceHandler(params int[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<HttpMethod> Methods { get; } = [];
        public Action? AfterResponse { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            var response = new HttpResponseMessage((HttpStatusCode)statuses[Calls++]) { Content = new StringContent("{\"value\":[]}") };
            AfterResponse?.Invoke();
            return Task.FromResult(response);
        }
    }
}
