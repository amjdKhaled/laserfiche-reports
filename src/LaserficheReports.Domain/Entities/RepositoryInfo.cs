namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Describes a Laserfiche repository and the API server that provides access to it.
/// </summary>
public sealed record RepositoryInfo
{
    /// <summary>Repository identifier used in all API requests, e.g. <c>Documents</c>.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>Human-readable name of the repository as configured on the Laserfiche Server.</summary>
    public string RepositoryName { get; init; } = string.Empty;

    /// <summary>Version string of the Laserfiche Server hosting this repository.</summary>
    public string ServerVersion { get; init; } = string.Empty;

    /// <summary>Version of the Laserfiche Repository API being used, e.g. <c>v2</c>.</summary>
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>Whether the LFDS authorization_code authentication flow is available.</summary>
    public bool SupportsAuthorizationCodeFlow { get; init; }
}
