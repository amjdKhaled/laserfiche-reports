using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Http;

/// <summary>
/// Logs the URL inputs immediately before each Laserfiche HTTP request is sent.
/// No credentials or authorization headers are logged.
/// </summary>
internal sealed class LaserficheRequestLoggingHandler : DelegatingHandler
{
    private readonly IOptionsMonitor<LaserficheOptions> _optionsMonitor;
    private readonly ILogger<LaserficheRequestLoggingHandler> _logger;

    public LaserficheRequestLoggingHandler(
        IOptionsMonitor<LaserficheOptions> optionsMonitor,
        ILogger<LaserficheRequestLoggingHandler> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;

        _logger.LogInformation(
            "Laserfiche request: ServerUrl={ServerUrl}, ApiBasePath={ApiBasePath}, " +
            "Final Request URL={FinalRequestUrl}",
            options.ServerUrl,
            options.ApiBasePath,
            request.RequestUri?.GetLeftPart(UriPartial.Path));

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Laserfiche response: HTTP {StatusCode} for {Method} {RequestPath}",
            (int)response.StatusCode, request.Method, request.RequestUri?.AbsolutePath);
        return response;
    }

}
