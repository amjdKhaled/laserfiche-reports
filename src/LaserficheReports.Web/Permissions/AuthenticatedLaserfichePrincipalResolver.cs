using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;

namespace LaserficheReports.Web.Permissions;

internal sealed class AuthenticatedLaserfichePrincipalResolver(IHttpContextAccessor contexts,
    ISessionCredentialStore sessions, IRepositoryContext repositories, ILaserficheAuthService auth)
    : IAuthenticatedLaserfichePrincipalResolver
{
    public async Task<AuthenticatedPermissionPrincipal?> ResolveAsync(PermissionRepositoryBinding binding, CancellationToken ct)
    {
        var session = contexts.HttpContext?.Session;
        var generation = session?.GetString("ReportsGeneration");
        var subject = session?.GetString("AuthenticationScopeSubject");
        if (session == null || string.IsNullOrEmpty(generation) || string.IsNullOrEmpty(subject) ||
            await sessions.TryGetSignedInUserAsync(ct) == null) return null;
        var repository = await repositories.GetActiveRepositoryAsync(ct);
        var token = await auth.GetTokenAsync(repository, ct); // Session-scoped server-issued token; never a browser JWT.
        var repositoryScope = repository.ServerUrl.TrimEnd('/').ToUpperInvariant() + "\n" + repository.RepositoryId.ToUpperInvariant() + "\n" + repository.Key;
        var scope = Hash(repositoryScope + "\n" + session.Id + "\n" + generation + "\n" + subject);
        if (session.GetString("AuthenticationScopeMethod") == "LFDS")
        {
            var identity = ReadSubject(token);
            if (identity == null) return null; // Opaque token / missing stable subject: no username fallback.
            var matches = binding.Subjects.Where(m => m.Issuer == identity.Value.Issuer &&
                m.Subject == identity.Value.Subject && !string.IsNullOrWhiteSpace(m.TrusteeSid)).ToArray();
            if (matches.Length != 1) return null;
            return new(scope, Hash(repositoryScope + "\n" + matches[0].TrusteeSid), null, matches[0].TrusteeSid);
        }
        if (session.GetString("AuthenticationScopeMethod") != "Reports") return null;
        var credential = await sessions.TryGetAsync(ct);
        return credential == null ? null : new(scope, Hash(repositoryScope + "\n" + credential.Username.ToUpperInvariant()), credential, null);
    }

    // This decoder is NOT a general JWT authenticator. Its only caller supplies a token
    // received by the existing trusted Repository API auth service, scoped to this session.
    internal static (string Issuer, string Subject)? ReadSubject(string token)
    {
        if (token.Length > 65536) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var value = parts[1].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(value));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("iss", out var issuer) ||
                !root.TryGetProperty("sub", out var subject) || issuer.ValueKind != JsonValueKind.String ||
                subject.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(issuer.GetString()) ||
                string.IsNullOrWhiteSpace(subject.GetString())) return null;
            return (issuer.GetString()!, subject.GetString()!);
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
