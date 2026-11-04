using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LaserficheReports.Web.Permissions;

internal sealed record ProtectedRead(string Url, string? Fingerprint);
internal sealed class ProtectedReadTracker
{
    private readonly ConcurrentDictionary<string, ProtectedRead> reads = new(StringComparer.Ordinal);
    internal ProtectedRead[] Snapshot() => reads.Values.ToArray();
    internal static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    internal async Task RecordAsync(Uri uri, HttpResponseMessage response, bool fingerprint, CancellationToken ct)
    {
        string? hash = null;
        if (fingerprint)
        {
            await response.Content.LoadIntoBufferAsync(33554432);
            hash = Hash(await response.Content.ReadAsStringAsync(ct));
        }
        var item = new ProtectedRead(uri.AbsoluteUri, hash);
        if (reads.TryGetValue(item.Url, out var previous) && previous.Fingerprint != item.Fingerprint)
            throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable));
        if (reads.Count >= 20000) throw new PermissionVerificationException(new(PermissionStatus.VerificationUnavailable));
        reads[item.Url] = item;
    }
    internal static async Task<bool> RevalidateAsync(ProtectedRead[] evidence, IHttpClientFactory clients, CancellationToken ct)
    {
        using var client = clients.CreateClient("LaserficheAuthenticated");
        foreach (var item in evidence)
        {
            // Same guarded transport and interactive token; actual field/content 403 is decisive.
            using var response = await client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;
            if (item.Fingerprint != null && Hash(await response.Content.ReadAsStringAsync(ct)) != item.Fingerprint) return false;
        }
        return true;
    }
}
