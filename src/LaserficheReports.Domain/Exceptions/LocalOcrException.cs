namespace LaserficheReports.Domain.Exceptions;

/// <summary>
/// Indicates that the configured local OCR worker could not process a request.
/// This is an infrastructure failure, not an empty or unreadable document page.
/// </summary>
public sealed class LocalOcrException : Exception
{
    public LocalOcrException(string message)
        : base(message)
    {
    }

    public LocalOcrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
