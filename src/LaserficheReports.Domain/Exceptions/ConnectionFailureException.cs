namespace LaserficheReports.Domain.Exceptions;

/// <summary>
/// Thrown when the Laserfiche API Server cannot be reached at all — network failure,
/// DNS resolution failure, timeout before a connection is established, or the API Server
/// is not installed. Distinct from <see cref="LaserficheException"/>, which represents
/// a reachable API that returned an error response.
/// </summary>
public sealed class ConnectionFailureException : Exception
{
    /// <summary>Initialises a new instance with a descriptive message.</summary>
    public ConnectionFailureException(string message) : base(message) { }

    /// <summary>Initialises a new instance wrapping an underlying network exception.</summary>
    public ConnectionFailureException(string message, Exception innerException)
        : base(message, innerException) { }
}
