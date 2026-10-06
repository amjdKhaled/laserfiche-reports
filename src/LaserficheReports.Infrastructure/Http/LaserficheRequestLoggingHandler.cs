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
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Stage=LASERFICHE_HTTP Method={Method} Status={Status} DurationMs={DurationMs}",
            request.Method.Method, (int)response.StatusCode, watch.ElapsedMilliseconds);
        // Do not buffer or log bodies: they can contain credentials, OCR text or sensitive metadata.
        return response;
    }

}
