using System.Security.Cryptography;
using LaserficheReports.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace LaserficheReports.Web.Permissions;

// Stores dependencies, never report bodies or content. Opaque, short-lived and bound to a server principal.
internal sealed class ReportAuthorizationReceipts(AuthorizationReceiptCache store, IRepositoryContext repositories,
    IAuthenticatedLaserfichePrincipalResolver principals, IConfiguration configuration,
    IFullHierarchyAuthorizationService gateway, RecycleBinSource recycleBin, ProtectedReadTracker reads, IHttpClientFactory clients)
{
    private sealed record Receipt(string Scope, string Repository, int[] Ids, string? RecycleFingerprint, ProtectedRead[] Reads);
    private static string Fingerprint(RecycleBinSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(snapshot.Entries.OrderBy(e => e.EntryId)))));
    private async Task<(string Scope, string Repository)> ScopeAsync(CancellationToken ct)
    {
        var repo = await repositories.GetActiveRepositoryAsync(ct);
        var bindings = configuration.GetSection("PermissionVerification:Repositories").GetChildren()
            .Where(b => string.Equals(b.Key, repo.RepositoryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var binding = bindings.Length == 1 ? bindings[0].Get<PermissionRepositoryBinding>() : null;
        var principal = binding == null ? null : await principals.ResolveAsync(binding, ct);
        if (principal == null) throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable));
        return (principal.Scope, repo.ServerUrl.TrimEnd('/').ToUpperInvariant() + "|" + repo.RepositoryId.ToUpperInvariant() + "|" + repo.Key);
    }
    internal async Task<string> IssueAsync(ChatResult result, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        var ids = gateway.AuthorizedEntryIds;
        // Recheck after model execution, before returning any protected answer.
        foreach (var id in ids) if (!await gateway.CanReadAsync(id, ct))
            throw new PermissionVerificationException(new(PermissionStatus.AncestorAccessDenied));
        var dependencies = reads.Snapshot();
        if (!await ProtectedReadTracker.RevalidateAsync(dependencies, clients, ct))
            throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable));
        var recycle = result.Scope?.Mode == "live-recycle-bin" ? Fingerprint(await recycleBin.ReadAsync(ct)) : null;
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        store.Cache.Set("hierarchy-receipt:" + key, new Receipt(scope.Scope, scope.Repository, ids, recycle, dependencies),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                Size = 1024L + ids.Length * 4L + dependencies.Sum(d => 128L + d.Url.Length * 2L + (d.Fingerprint?.Length ?? 0) * 2L) });
        return key;
    }
    internal async Task<bool> ValidateAsync(string? key, CancellationToken ct)
    {
        if (key is not { Length: 64 } || !store.Cache.TryGetValue("hierarchy-receipt:" + key, out Receipt? receipt) || receipt == null) return false;
        var scope = await ScopeAsync(ct);
        if (scope.Scope != receipt.Scope || scope.Repository != receipt.Repository) return false;
        if (receipt.RecycleFingerprint != null && Fingerprint(await recycleBin.ReadAsync(ct)) != receipt.RecycleFingerprint) return false;
        foreach (var id in receipt.Ids) if (!await gateway.CanReadAsync(id, ct)) return false;
        return await ProtectedReadTracker.RevalidateAsync(receipt.Reads, clients, ct);
    }
}
internal sealed record ReportRevalidationRequest(string? AuthorizationReceipt);

// Bounded dependency storage: eviction simply requires rerunning the report.
internal sealed class AuthorizationReceiptCache : IDisposable
{
    internal MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });
    public void Dispose() => Cache.Dispose();
}
