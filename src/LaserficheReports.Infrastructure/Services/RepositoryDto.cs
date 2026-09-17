using System.Text.Json.Serialization;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Exact response item returned by Laserfiche v1 GET /Repositories.
/// </summary>
public sealed class RepositoryDto
{
    [JsonPropertyName("repoId")]
    public string RepoId { get; set; } = string.Empty;

    [JsonPropertyName("repoName")]
    public string RepoName { get; set; } = string.Empty;

    [JsonPropertyName("webclientUrl")]
    public string WebclientUrl { get; set; } = string.Empty;
}