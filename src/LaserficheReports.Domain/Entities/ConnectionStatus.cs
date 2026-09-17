namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Represents the result of a Laserfiche connection health check at a specific point in time.
/// </summary>
public sealed record ConnectionStatus
{
    /// <summary><c>true</c> if the connection check completed without error.</summary>
    public bool IsConnected { get; init; }

    /// <summary><c>true</c> when token authentication completed successfully.</summary>
    public bool AuthenticationSucceeded { get; init; }

    /// <summary><c>true</c> when the configured repository was found in GET /Repositories.</summary>
    public bool RepositoryFound { get; init; }

    /// <summary>Repository ID that was tested. Null when connection failed before reaching the repository.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>Display name of the repository. Null on failure.</summary>
    public string? RepositoryName { get; init; }

    /// <summary>Version string of the Laserfiche Server. Null on failure.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>API version string, e.g. <c>v2</c>. Null on failure.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>UTC time at which the check was performed.</summary>
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Human-readable error description when <see cref="IsConnected"/> is <c>false</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a successful status record from repository information.</summary>
    public static ConnectionStatus Success(RepositoryInfo info) => new()
    {
        IsConnected = true,
        AuthenticationSucceeded = true,
        RepositoryFound = true,
        RepositoryId = info.RepositoryId,
        RepositoryName = info.RepositoryName,
        ServerVersion = info.ServerVersion,
        ApiVersion = info.ApiVersion,
        CheckedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Creates a failed status record with a descriptive error message.</summary>
    public static ConnectionStatus Failure(string error) => new()
    {
        IsConnected = false,
        ErrorMessage = error,
        CheckedAt = DateTimeOffset.UtcNow
    };
}
