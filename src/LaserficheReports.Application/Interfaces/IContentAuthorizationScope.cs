namespace LaserficheReports.Application.Interfaces;

// Unique per HTTP request: OCR refreshed for one user cannot be replaced by another user's chunks.
public interface IContentAuthorizationScope { string Scope { get; } }
