using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Exceptions;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Sends page images to a PaddleOCR-VL worker bound to the loopback interface.
/// The loopback-only validation prevents document content from being sent to a
/// remote OCR endpoint through configuration mistakes.
/// </summary>
internal sealed class PaddleOcrLocalService : ILocalOcrService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PaddleOcrOptions _options;
    private readonly ILogger<PaddleOcrLocalService> _logger;

    public PaddleOcrLocalService(
        IHttpClientFactory httpClientFactory,
        IOptions<PaddleOcrOptions> options,
        ILogger<PaddleOcrLocalService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> TryExtractTextAsync(
        Stream imageContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageContent);
        if (!_options.Enabled) return null;

        ValidateLoopbackBaseUrl(_options.BaseUrl);

        byte[] imageBytes;
        try
        {
            imageBytes = await ReadImageAsync(
                imageContent,
                _options.EffectiveMaxImageBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            _logger.LogWarning(exception, "Local PaddleOCR-VL rejected a page image.");
            return null;
        }

        if (imageBytes.Length == 0) return null;

        try
        {
            var client = _httpClientFactory.CreateClient("PaddleOcr");
            using var requestContent = new ByteArrayContent(imageBytes);
            requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.PostAsync("ocr", requestContent, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var details = await ReadLimitedErrorAsync(response, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Local PaddleOCR-VL returned HTTP {StatusCode}. Details: {Details}",
                    (int)response.StatusCode,
                    details);
                throw new LocalOcrException(
                    $"PaddleOCR worker returned HTTP {(int)response.StatusCode}: {details}");
            }

            var result = await response.Content.ReadFromJsonAsync<PaddleOcrResponse>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            var text = NormalizeText(result?.Text);
            if (text.Length < _options.EffectiveMinimumTextLength) return null;

            _logger.LogInformation(
                "Local OCR completed with {Engine} ({Model}); extracted {CharacterCount} characters.",
                result?.Engine ?? "PaddleOCR-VL",
                result?.Model ?? "unknown",
                text.Length);
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Local PaddleOCR-VL timed out after {TimeoutSeconds} seconds.",
                _options.EffectiveTimeoutSeconds);
            throw new LocalOcrException(
                $"PaddleOCR timed out after {_options.EffectiveTimeoutSeconds} seconds. " +
                "Check that the worker is still running.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "Local PaddleOCR-VL is unavailable at {BaseUrl}. Start tools/paddleocr-vl/start.ps1.",
                _options.BaseUrl);
            throw new LocalOcrException(
                $"PaddleOCR is unavailable at {_options.BaseUrl}. Start tools/paddleocr-vl/start.ps1.",
                exception);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Local PaddleOCR-VL returned an invalid response.");
            throw new LocalOcrException("PaddleOCR returned an invalid JSON response.", exception);
        }
    }

    internal static Uri ValidateLoopbackBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Ocr:BaseUrl must be an absolute HTTP URL.");
        }

        if (!uri.IsLoopback)
        {
            throw new InvalidOperationException(
                "Ocr:BaseUrl must use localhost or a loopback address. Remote OCR endpoints are not allowed.");
        }

        return uri;
    }

    internal static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ');
        var result = new StringBuilder(normalized.Length);
        var consecutiveBlankLines = 0;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = RemoveDirectionalControls(rawLine).TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (result.Length > 0 && consecutiveBlankLines == 0)
                    result.Append('\n');
                consecutiveBlankLines++;
                continue;
            }

            if (result.Length > 0) result.Append('\n');
            result.Append(line);
            consecutiveBlankLines = 0;
        }

        return result.ToString().Trim();
    }

    private static string RemoveDirectionalControls(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\u200E' or '\u200F' or '\u202A' or '\u202B' or '\u202C' or
                '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069' or
                '\uFEFF' or '\uFFFD')
            {
                continue;
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static async Task<byte[]> ReadImageAsync(
        Stream input,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var bytesRead = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) break;
            if (output.Length + bytesRead > maximumBytes)
                throw new InvalidDataException($"OCR page image exceeds the {maximumBytes} byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static async Task<string> ReadLimitedErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(details)) return "(none)";
        details = details.Trim();
        return details.Length <= 500 ? details : details[..500];
    }

    private sealed record PaddleOcrResponse(string? Text, string? Engine, string? Model);
}
