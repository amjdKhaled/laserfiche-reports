namespace LaserficheReports.Domain.Exceptions;

/// <summary>
/// Thrown when the Laserfiche Repository API returns an error response.
/// Callers can inspect <see cref="StatusCode"/> and <see cref="LFErrorCode"/> to determine
/// whether to retry, report a user-facing error, or escalate.
/// </summary>
public sealed class LaserficheException : Exception
{
    /// <summary>HTTP status code returned by the Laserfiche API, e.g. 401, 403, 404.</summary>
    public int StatusCode { get; }

    /// <summary>
    /// Laserfiche-specific error code from the response body, if available.
    /// Null when the API returned an error without a structured body.
    /// </summary>
    public string? LFErrorCode { get; }

    /// <summary>
    /// Sanitized Laserfiche response body, included when the API returned an error.
    /// Sensitive fields (access_token, refresh_token, password) are replaced with
    /// <c>[REDACTED]</c>. Null when no body was received or parsing failed.
    /// </summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Short diagnostic identifier (8 hex chars, uppercase) generated at the point
    /// of failure. Present on non-2xx responses so administrators can correlate the
    /// user-facing "Diagnostic ID: XXXXXXXX" message with the Error-level server log
    /// entry that contains the full sanitized response body.
    /// </summary>
    public string? DiagnosticId { get; }

    /// <summary>
    /// Initialises a new instance with an error message, HTTP status code, and optional
    /// Laserfiche error code, response body, and diagnostic ID.
    /// </summary>
    public LaserficheException(
        string  message,
        int     statusCode,
        string? lfErrorCode  = null,
        string? responseBody = null,
        string? diagnosticId = null)
        : base(message)
    {
        StatusCode   = statusCode;
        LFErrorCode  = lfErrorCode;
        ResponseBody = responseBody;
        DiagnosticId = diagnosticId;
    }

    /// <summary>
    /// Initialises a new instance with an inner exception that caused the failure.
    /// </summary>
    public LaserficheException(string message, int statusCode, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
