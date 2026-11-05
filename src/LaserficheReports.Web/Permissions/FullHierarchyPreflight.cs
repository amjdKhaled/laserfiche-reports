using LaserficheReports.Application.DTOs;

namespace LaserficheReports.Web.Permissions;

// Safe, user-scoped deployment diagnostics. No trustee, credential, SID, target
// entry or service-account details are returned to the browser.
internal static class FullHierarchyPreflight
{
    internal static string Check(RepositoryDescriptor repository, PermissionVerificationOptions options, bool bridgeReady)
    {
        var matches = options.Repositories.Where(item =>
            string.Equals(item.Key, repository.RepositoryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return "repository_binding_missing_or_duplicate";
        var binding = matches[0].Value;
        if (binding == null || !string.Equals(binding.ApiServerUrl?.TrimEnd('/'), repository.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(binding.ServerName) || string.IsNullOrWhiteSpace(binding.RepositoryName))
            return "repository_binding_invalid";
        if (!bridgeReady) return "sdk_bridge_payload_missing_or_platform_unsupported";
        return "ready_for_live_validation";
    }
}
