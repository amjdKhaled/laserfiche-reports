using System.Text;
using System.Text.Json;
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
            request.RequestUri);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Laserfiche response: HTTP {StatusCode} {ReasonPhrase} for {Method} {RequestUrl}. " +
            "Response body: {ResponseBody}",
            (int)response.StatusCode,
            response.ReasonPhrase,
            request.Method,
            request.RequestUri,
            RedactSensitiveJson(responseBody));

        // Reading the content above consumes it. Replace it so the service layer
        // receives the exact same response body after it has been logged.
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        response.Content = new StringContent(
            responseBody,
            Encoding.UTF8,
            mediaType);

        return response;
    }

    private static string RedactSensitiveJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return body;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("refresh_token", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("password", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteString(property.Name, "[REDACTED]");
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }
}