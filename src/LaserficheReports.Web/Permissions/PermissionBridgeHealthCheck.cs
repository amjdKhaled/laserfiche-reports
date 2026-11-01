using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LaserficheReports.Web.Permissions;

// Deployment health only. Connectivity and trustee decisions are validated on each request,
// not under a background configured user that could accidentally authorize another session.
internal sealed class PermissionBridgeHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("PermissionVerification:Enabled"))
            return Task.FromResult(HealthCheckResult.Healthy("Permission verification disabled."));
        var installed = OperatingSystem.IsWindows() && Environment.Is64BitProcess && File.Exists(ProcessLaserficheSdkBridge.ExecutablePath);
        return Task.FromResult(installed
            ? HealthCheckResult.Healthy("Bridge payload present; live SDK connectivity, licensing and trustee acceptance are not established by this deployment check.")
            : HealthCheckResult.Unhealthy("Permission bridge requires Windows x64 and its published payload."));
    }
}
