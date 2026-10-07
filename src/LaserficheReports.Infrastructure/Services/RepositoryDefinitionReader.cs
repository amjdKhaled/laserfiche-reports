using System.Net;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>Retries a transient gateway failure once, only for safe schema reads.</summary>
internal static class RepositoryDefinitionReader
{
    internal static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string url, ILogger logger, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
            return response;

        var status = (int)response.StatusCode;
        response.Dispose();
        logger.LogWarning("Repository schema read returned HTTP {Status}; retrying once.", status);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        return await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
    }
}
