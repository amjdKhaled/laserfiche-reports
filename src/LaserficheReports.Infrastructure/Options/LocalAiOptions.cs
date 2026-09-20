namespace LaserficheReports.Infrastructure.Options;

internal sealed class LocalAiOptions
{
    public const string SectionName = "LocalAI";

    public string Provider { get; init; } = "Ollama";
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string ChatModel { get; init; } = string.Empty;
    public string EmbeddingModel { get; init; } = "nomic-embed-text-v2-moe";
    public int EmbeddingDimensions { get; init; } = 768;
    public int EmbeddingBatchSize { get; init; } = 16;
    public int TimeoutSeconds { get; init; } = 120;
    public int ChunkSize { get; init; } = 1200;
    public int ChunkOverlap { get; init; } = 200;
    public string DocumentEmbeddingPrefix { get; init; } = "search_document: ";
    public string QueryEmbeddingPrefix { get; init; } = "search_query: ";

    public int EffectiveEmbeddingDimensions => EmbeddingDimensions > 0 ? EmbeddingDimensions : 768;
    public int EffectiveEmbeddingBatchSize => Math.Clamp(EmbeddingBatchSize, 1, 64);
    public int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 10, 600);
    public int EffectiveChunkSize => Math.Clamp(ChunkSize, 200, 8000);
    public int EffectiveChunkOverlap => Math.Clamp(ChunkOverlap, 0, EffectiveChunkSize / 2);
}
