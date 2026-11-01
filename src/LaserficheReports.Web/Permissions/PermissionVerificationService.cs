using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Web.Permissions;

// A fixed-size, process-wide admission table avoids attacker-controlled dictionary growth.
// No cached authorization decisions. Limits survive account logout/re-login and repository changes.
internal sealed class PermissionVerificationGate : IDisposable
{
    private readonly (DateTimeOffset Start, int Count)[] buckets = new (DateTimeOffset, int)[256];
    internal readonly SemaphoreSlim Concurrency = new(4, 4);
    internal bool Admit(string key, DateTimeOffset now)
    {
        var bucket = (StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % buckets.Length;
        lock (buckets)
        {
            if (now - buckets[bucket].Start >= TimeSpan.FromMinutes(1)) buckets[bucket] = (now, 0);
            if (buckets[bucket].Count >= 12) return false;
            buckets[bucket].Count++;
            return true;
        }
    }
    public void Dispose() => Concurrency.Dispose();
}

internal sealed class PermissionVerificationService(IConfiguration configuration, IRepositoryContext repositories,
    IAuthenticatedLaserfichePrincipalResolver principals, ILaserficheSdkBridge bridge,
    PermissionVerificationGate gate, ILogger<PermissionVerificationService> logger) : ILaserfichePermissionVerifier
{
    public async Task<PermissionDecision> VerifyAsync(PermissionTarget target, CancellationToken ct)
    {
        if (!target.IsValid) throw new ArgumentException("حدد عنصرًا واحدًا برقم صالح أو اسم أو مسار دقيق.");
        var options = configuration.GetSection("PermissionVerification").Get<PermissionVerificationOptions>() ?? new();
        if (!options.Enabled) return new(PermissionStatus.Unknown);
        var repository = await repositories.GetActiveRepositoryAsync(ct);
        var bindings = options.Repositories.Where(b => string.Equals(b.Key, repository.RepositoryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bindings.Length != 1) return new(PermissionStatus.VerificationUnavailable);
        var binding = bindings[0].Value;
        if (!string.Equals(binding.ApiServerUrl.TrimEnd('/'), repository.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(binding.ServerName) || string.IsNullOrWhiteSpace(binding.RepositoryName))
            return new(PermissionStatus.VerificationUnavailable);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 2, 30)));
        var acquired = false;
        var outcome = new PermissionDecision(PermissionStatus.VerificationUnavailable);
        try
        {
            var principal = await principals.ResolveAsync(binding, budget.Token);
            if (principal == null) return outcome = new(PermissionStatus.Unknown);
            if (!gate.Admit(principal.RateKey, DateTimeOffset.UtcNow)) return outcome = new(PermissionStatus.Unknown);
            await gate.Concurrency.WaitAsync(budget.Token);
            acquired = true;
            return outcome = await bridge.VerifyAsync(target, principal, repository.RepositoryId, binding, budget.Token);
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (LaserficheReports.Domain.Exceptions.LaserficheException error) when (error.StatusCode != 401) { return outcome; }
        catch (HttpRequestException) { return outcome; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return outcome; }
        finally
        {
            if (acquired) gate.Concurrency.Release();
            // Never log target, principal, password, token, SID, ACL, hidden counts or native error text.
            logger.LogInformation("PermissionVerification Status={Status} TraceId={TraceId}", outcome.Status,
                System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "local");
        }
    }
}
