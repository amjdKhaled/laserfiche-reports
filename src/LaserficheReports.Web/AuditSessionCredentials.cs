using System.Security.Cryptography;
using System.Text.Json;
using LaserficheReports.Domain.Common;
using Microsoft.AspNetCore.DataProtection;

namespace LaserficheReports.Web;

/// <summary>Separate Windows identity for this browser session, never written to configuration.</summary>
internal sealed class AuditSessionCredentials(IHttpContextAccessor context, IDataProtectionProvider protection)
{
    internal const string SessionKey = "AuditTrail.WindowsCredential";
    private readonly IDataProtector protector = protection.CreateProtector("LaserficheReports.AuditTrail.Session.v1");
    internal LaserficheCredential? Read()
    {
        var value = context.HttpContext?.Session.GetString(SessionKey);
        if (value == null) return null;
        try { return JsonSerializer.Deserialize<LaserficheCredential>(protector.Unprotect(value)); }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { context.HttpContext?.Session.Remove(SessionKey); return null; }
    }
    internal void Store(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || password.Length is < 1 or > 4096 ||
            !(username.Contains('\\') || username.Contains('@')))
            throw new ArgumentException("أدخل حساب Windows بصيغة DOMAIN\\user أو user@domain وكلمة مروره.");
        context.HttpContext!.Session.SetString(SessionKey,
            protector.Protect(JsonSerializer.Serialize(new LaserficheCredential(username.Trim(), password))));
    }
}
