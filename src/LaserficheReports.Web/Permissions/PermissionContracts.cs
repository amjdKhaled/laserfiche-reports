using LaserficheReports.Domain.Common;

namespace LaserficheReports.Web.Permissions;

internal enum PermissionStatus
{
    Accessible, DocumentAccessDenied, FolderAccessDenied, BrowseOnly, NotFound,
    Ambiguous, Unknown, AuthenticationRequired, VerificationUnavailable
}

// No names, paths, ACLs, counts or service identities leave the bridge.
internal sealed record PermissionDecision(PermissionStatus Status, string? EntryKind = null)
{
    internal const string Uncertain = "تعذر التحقق من وجود العنصر أو صلاحيات الوصول إليه حاليًا.";
    internal int HttpStatusCode => Status switch
    {
        PermissionStatus.Accessible => 200,
        PermissionStatus.DocumentAccessDenied or PermissionStatus.FolderAccessDenied or PermissionStatus.BrowseOnly => 403,
        PermissionStatus.NotFound => 404,
        PermissionStatus.AuthenticationRequired => 401,
        PermissionStatus.Ambiguous => 409,
        _ => 503
    };
    internal string Message => Status switch
    {
        PermissionStatus.DocumentAccessDenied => "لا توجد لديك صلاحية على هذه الوثيقة.",
        PermissionStatus.FolderAccessDenied => "لا توجد لديك صلاحية على هذا المجلد.",
        PermissionStatus.BrowseOnly => "يمكنك رؤية العنصر، لكن لا توجد لديك صلاحية لقراءة محتواه.",
        PermissionStatus.NotFound when EntryKind == "document" => "الوثيقة المطلوبة غير موجودة.",
        PermissionStatus.NotFound when EntryKind == "folder" => "المجلد المطلوب غير موجود.",
        PermissionStatus.AuthenticationRequired => "انتهت جلسة Laserfiche. أعد تسجيل الدخول.",
        _ => Uncertain
    };
    internal ChatResult ToChatResult() => new(Message, []);
}

// OperationForbidden is evidence supplied ONLY by a backend catch of a real API 403.
internal sealed record PermissionTarget(int? Id = null, string? Name = null, string? Path = null, string Kind = "all", bool OperationForbidden = false)
{
    internal bool IsValid => (Id.HasValue ? 1 : 0) + (Name != null ? 1 : 0) + (Path != null ? 1 : 0) == 1 &&
        Id is not <= 0 && Kind is "document" or "folder" or "all" &&
        (Name == null || Safe(Name, 200)) && (Path == null || Safe(Path, 2048) && Path.StartsWith('\\'));
    private static bool Safe(string text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max &&
        !text.Any(char.IsControl) && text.IndexOfAny(['"', '{', '}', '[', ']', '*', '?']) < 0;
}

internal sealed class PermissionVerificationOptions
{
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public Dictionary<string, PermissionRepositoryBinding> Repositories { get; set; } = new();
}

internal sealed class PermissionRepositoryBinding
{
    public string ApiServerUrl { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string ServiceCredentialFile { get; set; } = "";
    // Explicit trustee AND entry allowlists, never enabled by default.
    public string[] AllowedTrusteeSids { get; set; } = [];
    public int[] AllowedEntryIds { get; set; } = [];
    // Administrator acceptance evidence, PLUS runtime privilege checks, is required for negative proof.
    public bool FullVisibilityValidated { get; set; }
    public PermissionSubjectMapping[] Subjects { get; set; } = [];
}

internal sealed class PermissionSubjectMapping
{
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TrusteeSid { get; set; } = "";
}

internal sealed record AuthenticatedPermissionPrincipal(string Scope, string RateKey,
    LaserficheCredential? Credential, string? TrusteeSid);
internal interface IAuthenticatedLaserfichePrincipalResolver
{
    Task<AuthenticatedPermissionPrincipal?> ResolveAsync(PermissionRepositoryBinding binding, CancellationToken ct);
}
internal interface ILaserficheSdkBridge
{
    Task<PermissionDecision> VerifyAsync(PermissionTarget target, AuthenticatedPermissionPrincipal principal,
        string repositoryId, PermissionRepositoryBinding binding, CancellationToken ct);
}
internal interface ILaserfichePermissionVerifier
{
    Task<PermissionDecision> VerifyAsync(PermissionTarget target, CancellationToken ct);
}
internal sealed class PermissionVerificationException(PermissionDecision decision) : Exception(decision.Message)
{
    internal PermissionDecision Decision { get; } = decision;
}
