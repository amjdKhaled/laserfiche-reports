using System.Net.Http.Json;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Services;

internal sealed class OllamaTextEmbeddingService : ITextEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly LocalAiOptions _options;
    private readonly ILogger<OllamaTextEmbeddingService> _logger;

    public OllamaTextEmbeddingService(
        IHttpClientFactory httpClientFactory,
        IOptions<LocalAiOptions> options,
        ILogger<OllamaTextEmbeddingService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Ollama");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return Array.Empty<float[]>();
        if (!string.Equals(_options.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LocalAI:Provider must be Ollama for local embeddings.");
        if (string.IsNullOrWhiteSpace(_options.EmbeddingModel))
            throw new InvalidOperationException("LocalAI:EmbeddingModel is missing.");

        var results = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(_options.EffectiveEmbeddingBatchSize))
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsJsonAsync(
                    "/api/embed",
                    new { model = _options.EmbeddingModel, input = batch },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new InvalidOperationException(
                    $"Could not connect to local Ollama at {_options.BaseUrl}. Ensure Ollama is running.",
                    exception);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Local Ollama embedding request failed with HTTP {(int)response.StatusCode}: {Limit(body)}");
                }

                results.AddRange(ParseEmbeddings(
                    body,
                    _options.EffectiveEmbeddingDimensions,
                    _options.EmbeddingModel));
            }
        }

        if (results.Count != texts.Count)
            throw new InvalidOperationException("Local Ollama returned a different number of embeddings than requested.");

        _logger.LogInformation(
            "Created {EmbeddingCount} local embeddings with model {EmbeddingModel}.",
            results.Count,
            _options.EmbeddingModel);
        return results;
    }

    internal static IReadOnlyList<float[]> ParseEmbeddings(
        string body,
        int expectedDimensions,
        string model)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("embeddings", out var embeddingsElement) ||
            embeddingsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Local Ollama embedding response did not contain embeddings.");
        }

        var embeddings = new List<float[]>();
        foreach (var embeddingElement in embeddingsElement.EnumerateArray())
        {
            var embedding = embeddingElement.EnumerateArray()
                .Select(value => value.GetSingle())
                .ToArray();

            if (embedding.Length != expectedDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding model {model} returned {embedding.Length} dimensions; " +
                    $"the database expects {expectedDimensions}.");
            }

            embeddings.Add(embedding);
        }

        return embeddings;
    }

    private static string Limit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(empty response)";
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }
}
