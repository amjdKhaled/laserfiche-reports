using System.Net.Http.Headers;
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
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        // Never decode image, PDF, or other file responses as text. Decoding a
        // PNG changes its leading 0x89 byte to the UTF-8 replacement sequence
        // EF-BF-BD and corrupts the download. Binary bodies pass through untouched.
        if (!IsTextContentType(mediaType))
        {
            _logger.LogInformation(
                "Laserfiche response: HTTP {StatusCode} {ReasonPhrase} for {Method} {RequestUrl}. " +
                "Binary response: ContentType={ContentType}; ContentLength={ContentLength}",
                (int)response.StatusCode,
                response.ReasonPhrase,
                request.Method,
                request.RequestUri,
                mediaType ?? "(missing)",
                response.Content.Headers.ContentLength);

            return response;
        }

        var originalHeaders = response.Content.Headers
            .Select(header => new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.ToArray()))
            .ToArray();
        var responseBytes = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var responseBody = GetTextEncoding(response.Content.Headers.ContentType)
            .GetString(responseBytes);

        _logger.LogInformation(
            "Laserfiche response: HTTP {StatusCode} {ReasonPhrase} for {Method} {RequestUrl}. " +
            "Response body: {ResponseBody}",
            (int)response.StatusCode,
            response.ReasonPhrase,
            request.Method,
            request.RequestUri,
            RedactSensitiveJson(responseBody));

        // Keep the original bytes and all content headers. Re-encoding the body
        // could change it even for non-UTF-8 textual Laserfiche responses.
        var replacement = new ByteArrayContent(responseBytes);
        foreach (var header in originalHeaders)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = replacement;

        return response;
    }

    private static bool IsTextContentType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding GetTextEncoding(MediaTypeHeaderValue? contentType)
    {
        var charset = contentType?.CharSet?.Trim('"');
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
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
