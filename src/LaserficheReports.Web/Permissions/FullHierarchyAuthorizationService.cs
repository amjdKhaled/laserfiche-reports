using System.Net.Http.Headers;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Infrastructure.Adapters;

namespace LaserficheReports.Web.Permissions;

internal interface IFullHierarchyAuthorizationService : IFullHierarchyPolicy
{
    Task<bool> CanReadAsync(int entryId, CancellationToken ct);
    int[] AuthorizedEntryIds { get; }
}

// Request-scoped coordinator. No positive authorization cache, privileged result data or disclosure allowlists.
internal sealed class FullHierarchyAuthorizationService(IConfiguration configuration,
    IHttpContextAccessor contexts, IRepositoryContext repositories, ISessionCredentialStore sessions,
    IAuthenticatedLaserfichePrincipalResolver principals, ILaserficheSdkBridge bridge,
    PermissionVerificationGate gate, IHttpClientFactory clients, ILaserficheApiAdapter adapter,
    ILaserficheAuthService auth, ILogger<FullHierarchyAuthorizationService> logger) : IFullHierarchyAuthorizationService
{
    private int checks;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> authorized = new();
    public int[] AuthorizedEntryIds => authorized.Keys.Order().ToArray();
    public async Task<bool> CanReadAsync(int entryId, CancellationToken ct)
    {
        var unavailable = new PermissionDecision(PermissionStatus.VerificationUnavailable);
        if (entryId <= 0 || Interlocked.Increment(ref checks) > 10000) throw new PermissionVerificationException(unavailable);
        var context = contexts.HttpContext ?? throw new PermissionVerificationException(unavailable);
        var repository = await repositories.GetActiveRepositoryAsync(ct);
        if (!await RepositoryReadSession.IsAuthorizedAsync(context, repository, sessions, ct))
            throw new PermissionVerificationException(new(PermissionStatus.AuthenticationRequired));
        var options = configuration.GetSection("PermissionVerification").Get<PermissionVerificationOptions>() ?? new();
        var bindings = options.Repositories.Where(b => string.Equals(b.Key, repository.RepositoryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bindings.Length != 1)
        {
            logger.LogWarning("FullHierarchy unavailable Reason=repository_binding_missing_or_duplicate TraceId={TraceId}", context.TraceIdentifier);
            throw new PermissionVerificationException(unavailable);
        }
        var binding = bindings[0].Value;
        if (!string.Equals(binding.ApiServerUrl.TrimEnd('/'), repository.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(binding.ServerName) || string.IsNullOrWhiteSpace(binding.RepositoryName))
        {
            logger.LogWarning("FullHierarchy unavailable Reason=repository_binding_invalid TraceId={TraceId}", context.TraceIdentifier);
            throw new PermissionVerificationException(unavailable);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 2, 30)));
        var acquired = false;
        try
        {
            var principal = await principals.ResolveAsync(binding, budget.Token);
            if (principal == null) throw new PermissionVerificationException(unavailable);
            // Diagnostic rate limits are intentionally separate: every candidate needs enforcement.
            await gate.Concurrency.WaitAsync(budget.Token);
            acquired = true;
            var decision = await bridge.VerifyAsync(new(Id: entryId, RequireFullAncestorAccess: true),
                principal, repository.RepositoryId, binding, budget.Token);
            logger.LogInformation("FullHierarchy Status={Status} TraceId={TraceId}", decision.Status, context.TraceIdentifier);
            if (decision.Status is PermissionStatus.AncestorAccessDenied or PermissionStatus.FolderAccessDenied or PermissionStatus.DocumentAccessDenied)
                return false;
            if (decision.Status != PermissionStatus.Accessible || decision.VerifiedChain is not { Length: > 0 and <= 256 } chain ||
                chain[0] != entryId || chain.Any(id => id <= 0) || chain.Distinct().Count() != chain.Length)
                throw new PermissionVerificationException(unavailable);
            // Effective rights alone cannot prove navigation (tags/filters). Read EVERY level using
            // the current interactive API token, never the bridge service identity.
            using var client = clients.CreateClient("LaserficheRaw");
            var token = await auth.GetTokenAsync(repository, budget.Token);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            for (var i = 0; i < chain.Length; i++)
            {
                using var response = await client.GetAsync(adapter.BuildEntryUrl(repository.RepositoryId, chain[i], EntryResource.Details), budget.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new PermissionVerificationException(new(PermissionStatus.AuthenticationRequired));
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return false;
                if (!response.IsSuccessStatusCode) throw new PermissionVerificationException(unavailable);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
                var node = json.RootElement;
                if (node.TryGetProperty("entry", out var wrapped)) node = wrapped;
                if (!node.TryGetProperty("id", out var id) || !id.TryGetInt32(out var actual) || actual != chain[i])
                    throw new PermissionVerificationException(unavailable);
                if (i + 1 < chain.Length && (!node.TryGetProperty("parentId", out var parent) ||
                    !parent.TryGetInt32(out var parentId) || parentId != chain[i + 1]))
                    throw new PermissionVerificationException(unavailable);
            }
            // Refuse a response if the authenticated session/repository changed during verification.
            var current = await repositories.GetActiveRepositoryAsync(budget.Token);
            var finalPrincipal = await principals.ResolveAsync(binding, budget.Token);
            if (current.Key != repository.Key || finalPrincipal?.Scope != principal.Scope)
                throw new PermissionVerificationException(unavailable);
            authorized.TryAdd(entryId, 0);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PermissionVerificationException(unavailable); }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        { throw new PermissionVerificationException(unavailable); }
        finally { if (acquired) gate.Concurrency.Release(); }
    }
}
