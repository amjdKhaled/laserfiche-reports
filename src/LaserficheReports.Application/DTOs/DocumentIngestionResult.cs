namespace LaserficheReports.Application.DTOs;

/// <summary>Result of indexing one Laserfiche document in the local Supabase database.</summary>
public sealed record DocumentIngestionResult(
    long DocumentRowId,
    int EntryId,
    string RepositoryId,
    string DocumentName,
    int MetadataFieldCount,
    bool WasInserted,
    string IngestionStatus,
    int ChunkCount,
    string? EmbeddingModel);
