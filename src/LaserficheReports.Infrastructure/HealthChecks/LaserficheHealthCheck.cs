using LaserficheReports.Application.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace LaserficheReports.Infrastructure.HealthChecks;

/// <summary>
/// ASP.NET Core health check that verifies the Laserfiche API Server is reachable
/// and the configured repository is accessible.
/// </summary>
/// <remarks>
/// Registered at the <c>/health</c> endpoint by <c>AddLaserficheInfrastructure()</c>.
/// Returns <see cref="HealthStatus.Healthy"/> when connected, <see cref="HealthStatus.Unhealthy"/>
/// on any error. Includes repository name and server version in the health check data
/// so monitoring tools can surface the current connection state.
/// </remarks>
internal sealed class LaserficheHealthCheck : IHealthCheck
{
    private readonly ILaserficheRepositoryService _repositoryService;
    private readonly ILogger<LaserficheHealthCheck> _logger;

    /// <summary>Initialises the health check with the repository service.</summary>
    public LaserficheHealthCheck(
        ILaserficheRepositoryService repositoryService,
        ILogger<LaserficheHealthCheck> logger)
    {
        _repositoryService = repositoryService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _repositoryService
                .TestConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (status.IsConnected)
            {
                var data = new Dictionary<string, object>
                {
                    ["repositoryId"]   = status.RepositoryId ?? "Unknown",
                    ["repositoryName"] = status.RepositoryName ?? "Unknown",
                    ["serverVersion"]  = status.ServerVersion ?? "Unknown",
                    ["apiVersion"]     = status.ApiVersion ?? "Unknown",
                    ["checkedAt"]      = status.CheckedAt.ToString("O")
                };

                return HealthCheckResult.Healthy(
                    "Laserfiche API Server is reachable and the repository is accessible.",
                    data);
            }

            _logger.LogWarning(
                "Health check: Laserfiche connection failed. {Error}",
                status.ErrorMessage);

            return HealthCheckResult.Unhealthy(
                $"Laserfiche connection failed: {status.ErrorMessage}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check threw an unexpected exception.");
            return HealthCheckResult.Unhealthy(
                "Health check encountered an unexpected error.",
                exception: ex);
        }
    }
}
