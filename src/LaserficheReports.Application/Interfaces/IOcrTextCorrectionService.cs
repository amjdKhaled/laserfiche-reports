namespace LaserficheReports.Application.Interfaces;

/// <summary>
/// Corrects obvious OCR character and spacing errors without changing factual content.
/// Implementations must return the original text when correction is unavailable or unsafe.
/// </summary>
public interface IOcrTextCorrectionService
{
    Task<OcrTextCorrectionResult> CorrectAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public sealed record OcrTextCorrectionResult(
    string Text,
    bool WasCorrected,
    string? Model,
    string? Diagnostic = null);
