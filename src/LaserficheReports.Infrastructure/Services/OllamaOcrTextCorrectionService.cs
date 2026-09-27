using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Evaluates local Ollama suggestions. Text-only changes are retained as unverified
/// and cannot replace source OCR until an image-backed verification path exists.
/// </summary>
internal sealed partial class OllamaOcrTextCorrectionService : IOcrTextCorrectionService
{
    private readonly HttpClient _httpClient;
    private readonly LocalAiOptions _options;
    private readonly ILogger<OllamaOcrTextCorrectionService> _logger;

    public OllamaOcrTextCorrectionService(
        IHttpClientFactory httpClientFactory,
        IOptions<LocalAiOptions> options,
        ILogger<OllamaOcrTextCorrectionService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Ollama");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OcrTextCorrectionResult> CorrectAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!_options.OcrCorrectionEnabled || string.IsNullOrWhiteSpace(text))
            return new(text, false, null, "disabled");
        var model = _options.OcrCorrectionModel.Trim();
        if (model.Length == 0)
            return new(text, false, null, "model-not-configured");

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/generate",
                new
                {
                    model,
                    stream = false,
                    format = "json",
                    options = new { temperature = 0, num_predict = _options.EffectiveOcrCorrectionMaxTokens },
                    prompt = BuildPrompt(text)
                },
                cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Local OCR correction returned HTTP {StatusCode}; the original OCR text is retained.",
                    (int)response.StatusCode);
                return new(text, false, model, $"http-{(int)response.StatusCode}");
            }

            var candidate = ParseCorrectedText(body);
            var validationError = ValidateCorrection(text, candidate);
            if (validationError is not null)
            {
                _logger.LogWarning(
                    "Rejected OCR correction from {Model}: {ValidationError}. Original OCR text retained.",
                    model,
                    validationError);
                return new(text, false, model, validationError);
            }

            candidate = candidate!.Trim();
            return new(candidate, !string.Equals(text.Trim(), candidate, StringComparison.Ordinal), model);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Local OCR correction is unavailable; the original OCR text is retained.");
            return new(text, false, model, "local-correction-unavailable");
        }
    }

    internal static string BuildPrompt(string text) => $$"""
        أنت مدقق OCR محافظ للنصوص العربية والإنجليزية. صحح فقط أخطاء التعرف البصري الواضحة في الحروف والمسافات، مثل:
        العتمدة ← المعتمدة، العنية ← المعنية، الحددة ← المحددة، اللكي ← الملكي، الجلس ← المجلس.

        قواعد إلزامية:
        1. لا تلخص، ولا تعيد الصياغة، ولا تضف أي معلومة.
        2. لا تغير أي رقم أو تاريخ أو رقم مادة أو قرار أو اسم علم.
        3. حافظ على ترتيب الفقرات والأسطر وعلامات Markdown والجداول.
        4. إذا كانت كلمة غير مؤكدة فاتركها كما هي.
        5. أعد JSON فقط بهذه الصورة: {"corrected_text":"..."}.

        النص:
        <ocr>
        {{text}}
        </ocr>
        """;

    internal static string? ParseCorrectedText(string body)
    {
        using var envelope = JsonDocument.Parse(body);
        if (!envelope.RootElement.TryGetProperty("response", out var responseElement) ||
            responseElement.ValueKind != JsonValueKind.String)
            return null;

        var inner = responseElement.GetString();
        if (string.IsNullOrWhiteSpace(inner)) return null;
        using var result = JsonDocument.Parse(inner);
        return result.RootElement.TryGetProperty("corrected_text", out var corrected) &&
               corrected.ValueKind == JsonValueKind.String
            ? corrected.GetString()
            : null;
    }

    internal static string? ValidateCorrection(string original, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "empty-result";

        var originalLength = original.Trim().Length;
        var candidateLength = candidate.Trim().Length;
        if (originalLength == 0) return "empty-source";
        var ratio = candidateLength / (double)originalLength;
        if (ratio is < 0.80 or > 1.20) return "length-changed-too-much";

        var originalNumbers = ProtectedNumberRegex().Matches(original)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        var candidateNumbers = ProtectedNumberRegex().Matches(candidate)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        if (!originalNumbers.SequenceEqual(candidateNumbers, StringComparer.Ordinal))
            return "numbers-or-dates-changed";

        if (candidate.Contains("```", StringComparison.Ordinal) ||
            candidate.Contains("<ocr>", StringComparison.OrdinalIgnoreCase))
            return "unexpected-wrapper";

        // This service receives text only. A fluent replacement (including a
        // proper name) cannot be verified against the source image here.
        if (!string.Equals(original.Trim(), candidate.Trim(), StringComparison.Ordinal))
            return "visual-verification-required";

        return null;
    }

    [GeneratedRegex(@"\d+(?:[\s./:\-]\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedNumberRegex();
}
