namespace LaserficheReports.Application.Interfaces;

/// <summary>Creates vector embeddings without sending text outside the local machine.</summary>
public interface ITextEmbeddingService
{
    /// <summary>Creates one embedding for each supplied text in the same order.</summary>
    Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
