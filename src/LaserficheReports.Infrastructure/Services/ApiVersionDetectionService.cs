using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaserficheReports.Infrastructure.Services;

/// <summary>
/// Detects which Laserfiche Repository API version the configured server exposes
/// when the administrator selects <c>Auto</c> (the default).
/// </summary>
/// <remarks>
/// <para>
/// WHY a background service: URL building is synchronous and happens on every
/// request, so detection cannot run inline. Instead this service probes once at
/// startup and again whenever the connection settings change, persisting the
/// result to the runtime settings file. The options pipeline reloads it, and
/// <see cref="LaserficheOptions.EffectiveApiVersion"/> resolves <c>Auto</c> to
/// the detected value everywhere — adapters, Settings page, diagnostics.
/// </para>
/// <para>
/// Probe order (per product requirement): <c>{ServerUrl}{ApiBasePath}/v2/Repositories</c>
/// first — if the server answers with a status indicating the route exists
/// (200, or an auth challenge 401/403), v2 is used. Otherwise v1 is probed the
/// same way. If neither answers, nothing is persisted and URLs fall back to v1
/// until the server becomes reachable and detection re-runs on the next
/// settings change or application start.
/// </para>
/// <para>
/// Loop safety: persisting the detected version triggers an options reload,
/// which re-enters <see cref="OnOptionsChangedAsync"/> — but the second pass
/// finds the detected value already recorded and writes nothing, so the cycle
/// terminates after one round trip.
/// </para>
/// </remarks>
internal sealed class ApiVersionDetectionService : BackgroundService
{
    /// <summary>Versions probed, in preference order (newest first).</summary>
    private static readonly string[] CandidateVersions = ["v2", "v1"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<LaserficheOptions> _optionsMonitor;
    private readonly IPortalConfigurationService _portalConfig;
    private readonly ILogger<ApiVersionDetectionService> _logger;

    private readonly SemaphoreSlim _detectLock = new(1, 1);

    public ApiVersionDetectionService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<LaserficheOptions> optionsMonitor,
        IPortalConfigurationService portalConfig,
        ILogger<ApiVersionDetectionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _portalConfig = portalConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // React to Settings-page saves / installer config changes at runtime.
        using var subscription = _optionsMonitor.OnChange((_, _) =>
            _ = OnOptionsChangedAsync(stoppingToken));

        // Initial detection at startup.
        await OnOptionsChangedAsync(stoppingToken).ConfigureAwait(false);

        // Keep the service alive so the OnChange subscription stays registered.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Runs detection when needed; never throws (logs instead).</summary>
    private async Task OnOptionsChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _detectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var options = _optionsMonitor.CurrentValue;

            if (!options.IsAutoApiVersion)
                return; // explicitly pinned to v1/v2 — nothing to detect

            if (!string.IsNullOrWhiteSpace(options.DetectedApiVersion))
                return; // already detected for the current connection settings

            if (string.IsNullOrWhiteSpace(options.ServerUrl))
            {
                _logger.LogDebug("[API VERSION] Server URL not configured yet; detection deferred.");
                return;
            }

            var detected = await ProbeAsync(options, cancellationToken).ConfigureAwait(false);
            if (detected is null)
            {
                _logger.LogWarning(
                    "[API VERSION] Auto-detect could not reach {ServerUrl}{ApiBasePath} on any known " +
                    "version; URLs fall back to v1 until the server is reachable.",
                    options.ServerUrl, options.ApiBasePath);
                return;
            }

            await _portalConfig.SaveDetectedApiVersionAsync(detected, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — nothing to log.
        }
        catch (Exception ex)
        {
            // Detection must never take the site down; the v1 fallback keeps it usable.
            _logger.LogError(ex, "[API VERSION] Auto-detect failed unexpectedly.");
        }
        finally
        {
            _detectLock.Release();
        }
    }

    /// <summary>
    /// Probes <c>{ServerUrl}{ApiBasePath}/{version}/Repositories</c> for each candidate
    /// version, newest first, and returns the first version whose route exists.
    /// </summary>
    private async Task<string?> ProbeAsync(LaserficheOptions options, CancellationToken cancellationToken)
    {
        var root     = options.ServerUrl.TrimEnd('/');
        var basePath = "/" + options.ApiBasePath.Trim('/');
        if (root.EndsWith(basePath, StringComparison.OrdinalIgnoreCase))
            root = root[..^basePath.Length].TrimEnd('/');

        var client = _httpClientFactory.CreateClient("LaserficheProbe");

        foreach (var version in CandidateVersions)
        {
            var probeUrl = $"{root}{basePath}/{version}/Repositories";
            if (await RouteExistsAsync(client, probeUrl, version, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("[API VERSION] Auto-detected {Version} at {ProbeUrl}.", version, probeUrl);
                return version;
            }
        }

        return null;
    }

    /// <summary>
    /// A route "exists" when:
    /// <list type="bullet">
    ///   <item>
    ///     The server returns 401/403 (auth challenge) — the route is definitely handled
    ///     by the Laserfiche API; the body is irrelevant.
    ///   </item>
    ///   <item>
    ///     The server returns 200 AND the body is a recognisable repository list (V1
    ///     plain-array <c>[{...}]</c> or V2 OData envelope <c>{"value":[...]}</c>).
    ///     A 200 with HTML or any other incompatible JSON shape is rejected — it indicates
    ///     the URL resolves to something other than the repository API (reverse-proxy health
    ///     page, error object, etc.) and must not select this version.
    ///   </item>
    /// </list>
    /// </summary>
    private async Task<bool> RouteExistsAsync(
        HttpClient client, string probeUrl, string version, CancellationToken cancellationToken)
    {
        try
        {
            // Use ResponseHeadersRead for the initial probe: lets us short-circuit on
            // auth challenges without buffering the response body unnecessarily.
            using var response = await client
                .GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;

            // Auth challenge: the route is handled by the Laserfiche API server.
            if (status is 401 or 403)
            {
                _logger.LogDebug(
                    "[API VERSION] Probe {ProbeUrl} → HTTP {Status} (auth challenge — {Version} available).",
                    probeUrl, status, version);
                return true;
            }

            if (status != 200)
            {
                _logger.LogDebug(
                    "[API VERSION] Probe {ProbeUrl} → HTTP {Status} ({Version} not available).",
                    probeUrl, status, version);
                return false;
            }

            // HTTP 200: read the body and validate the JSON shape.
            // Accepting 200 on header status alone was the root cause of auto-detect
            // selecting v2 even though the v2 OData response body was unrecognised.
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            RepositoryJsonParser.TryParse(body, out var shape);   // for logging only
            var compatible = RepositoryJsonParser.IsCompatibleShape(body);

            _logger.LogDebug(
                "[API VERSION] Probe {ProbeUrl} → HTTP 200, body shape={Shape}, compatible={Compatible} " +
                "({Version} {Verdict}).",
                probeUrl, shape, compatible, version,
                compatible ? "available" : "not available — body shape not a repository list");

            return compatible;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "[API VERSION] Probe {ProbeUrl} failed (transport).", probeUrl);
            return false;
        }
    }
}
