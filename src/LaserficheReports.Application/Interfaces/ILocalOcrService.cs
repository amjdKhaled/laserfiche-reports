namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Extracts text from a document page using an OCR engine running on the local machine.
/// Document content must never be sent to an external service.
/// </summary>
public interface ILocalOcrService
{
    /// <summary>
    /// Attempts to extract text from <paramref name="imageContent"/>.
    /// Returns <c>null</c> when OCR is disabled, unavailable, or produces no usable text.
    /// </summary>
    Task<string?> TryExtractTextAsync(
        Stream imageContent,
        CancellationToken cancellationToken = default);
}
