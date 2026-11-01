using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Laserfiche.RepositoryAccess;
using Newtonsoft.Json;

// No listener and no command line arguments. Only the backend owns these inherited pipes.
// The installed directory/credential file must be writable only by the service owner/admins.
internal static class Program
{
    private sealed class Target { public int? Id { get; set; } public string Name { get; set; } public string Path { get; set; } public string Kind { get; set; } }
    private sealed class Binding
    {
        public string ServerName { get; set; } public string RepositoryName { get; set; } public string ServiceCredentialFile { get; set; }
        public string[] AllowedTrusteeSids { get; set; } public int[] AllowedEntryIds { get; set; } public bool FullVisibilityValidated { get; set; }
        // Web-only fields are accepted by the shared envelope, never used to select an identity.
        public string ApiServerUrl { get; set; } public object Subjects { get; set; }
    }
    private sealed class Input
    {
        public string Protocol { get; set; } public string RequestId { get; set; } public string RepositoryId { get; set; } public string PrincipalScope { get; set; }
        public Target Target { get; set; } public Binding Binding { get; set; } public string Username { get; set; } public string Password { get; set; } public string TrusteeSid { get; set; }
    }
    private sealed class Credential { public string Username { get; set; } public string Password { get; set; } }
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
        MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 12 };
    private static int Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var text = ReadBounded(Console.In, 65536);
            var input = JsonConvert.DeserializeObject<Input>(text, JsonSettings);
            Validate(input);
            string kind;
            var status = Verify(input, out kind);
            Console.Write(JsonConvert.SerializeObject(new { protocol = "permission-v1", requestId = input.RequestId,
                repositoryId = input.RepositoryId, principalScope = input.PrincipalScope, status, entryKind = kind }));
            return 0;
        }
        catch (Exception)
        {
            // Native diagnostics can expose protected names/paths. Backend records only a status and trace ID.
            Console.Error.Write("verification_unavailable");
            return 1;
        }
    }
    private static string ReadBounded(TextReader reader, int limit)
    {
        var result = new StringBuilder(); var buffer = new char[1024]; int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        { if (result.Length + count > limit) throw new InvalidDataException(); result.Append(buffer, 0, count); }
        return result.ToString();
    }
    private static void Validate(Input i)
    {
        if (i == null || i.Protocol != "permission-v1" || string.IsNullOrEmpty(i.RequestId) || i.RequestId.Length != 32 ||
            string.IsNullOrEmpty(i.RepositoryId) || i.RepositoryId.Length > 256 ||
            string.IsNullOrEmpty(i.PrincipalScope) || i.PrincipalScope.Length != 64 || i.Target == null || i.Binding == null ||
            string.IsNullOrWhiteSpace(i.Binding.ServerName) || string.IsNullOrWhiteSpace(i.Binding.RepositoryName) ||
            i.Binding.AllowedEntryIds == null || i.Binding.AllowedTrusteeSids == null ||
            (i.Target.Id.HasValue ? 1 : 0) + (i.Target.Name != null ? 1 : 0) + (i.Target.Path != null ? 1 : 0) != 1 ||
            i.Target.Id <= 0 || (i.Target.Kind != "all" && i.Target.Kind != "document" && i.Target.Kind != "folder") ||
            i.Target.Name != null && !Safe(i.Target.Name, 200) ||
            i.Target.Path != null && (!Safe(i.Target.Path, 2048) || !i.Target.Path.StartsWith("\\")) ||
            (i.TrusteeSid == null && (string.IsNullOrWhiteSpace(i.Username) || i.Username.Length > 256 || i.Password == null || i.Password.Length > 4096)) ||
            (i.TrusteeSid != null && (i.Username != null || i.Password != null))) throw new ArgumentException();
    }
    private static bool Safe(string s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max &&
        !s.Any(char.IsControl) && s.IndexOfAny(new[] { '"', '{', '}', '[', ']', '*', '?' }) < 0;

    private static string Verify(Input input, out string kind)
    {
        kind = null;
        var registration = new RepositoryRegistration(input.Binding.ServerName, input.Binding.RepositoryName);
        string sid = input.TrusteeSid;
        if (sid == null)
        {
            // This is the interactive user's authenticated SDK identity, NEVER the service account.
            using (var user = new Session())
            {
                user.IsSecure = true;
                user.LogIn(input.Username, input.Password, registration);
                sid = user.UserIdentity.ToString();
            }
        }
        if (!input.Binding.AllowedTrusteeSids.Contains(sid, StringComparer.Ordinal)) return "Unknown";
        if (input.Target.Id.HasValue && !input.Binding.AllowedEntryIds.Contains(input.Target.Id.Value)) return "Unknown";
        using (var service = new Session())
        {
            service.IsSecure = true;
            if (string.IsNullOrWhiteSpace(input.Binding.ServiceCredentialFile))
                service.LogIn(registration); // Explicit deployment choice: Windows service identity.
            else
            {
                var credential = ReadServiceCredential(input.Binding.ServiceCredentialFile);
                service.LogIn(credential.Username, credential.Password, registration);
            }
            var trustee = new AccountReference(new SecurityIdentifier(sid), service);
            if (!trustee.IsUser || trustee.ToSecurityIdentifier().ToString() != sid) return "Unknown";
            // Resolve only one exact target. Never enumerate hidden rows or expose their names/counts.
            EntryInfo entry = null;
            try
            {
                if (input.Target.Path != null) entry = Entry.GetEntryInfo(input.Target.Path, service);
                else
                {
                    var command = input.Target.Id.HasValue ? "{LF:ID=" + input.Target.Id.Value + "}" :
                        "{LF:Name=\"" + input.Target.Name.Replace("\\", "\\\\") + "\", Type=" +
                        (input.Target.Kind == "folder" ? "F" : input.Target.Kind == "document" ? "D" : "DF") + "}";
                    using (var search = new Search(service, command))
                    {
                        search.Run();
                        var count = search.GetHitCount();
                        if (count == 0)
                        {
                            // Absence must never be inferred from an exception or a visibility-limited search.
                            var required = Privileges.ReadAccess | Privileges.BypassBrowse | Privileges.BypassFilters | Privileges.SeeAllUserAreas;
                            if (input.Target.Id.HasValue && input.Target.Kind != "all" && input.Binding.FullVisibilityValidated &&
                                (service.Privileges & required) == required)
                            { kind = input.Target.Kind; return "NotFound"; }
                            return "Unknown";
                        }
                        if (count != 1) return "Unknown"; // Does not reveal ambiguity involving hidden entries.
                        using (var listing = search.GetResultListing(new SearchListingSettings()))
                            entry = (EntryInfo)((IEntryListingBase)listing).GetEntryInfo(1);
                    }
                }
                if (!input.Binding.AllowedEntryIds.Contains(entry.Id)) return "Unknown";
                kind = entry.EntryType == EntryType.Document ? "document" :
                    entry.EntryType == EntryType.Folder || entry.EntryType == EntryType.RecordSeries ? "folder" : null;
                if (kind == null || input.Target.Kind != "all" && input.Target.Kind != kind) { kind = null; return "Unknown"; }
                var rights = Entry.GetEffectiveRights(entry.Id, trustee, service);
                var requiredRights = EntryRights.Browse | EntryRights.Read;
                if ((rights & requiredRights) != requiredRights)
                    return kind == "folder" ? "FolderAccessDenied" : "DocumentAccessDenied";
                // Rights alone don't prove success of the requested API read (tags, field/volume ACLs,
                // filters, concurrent moves). The real read remains mandatory under the user's session.
                kind = null;
                return "Unknown";
            }
            finally { if (entry != null) entry.Dispose(); }
        }
    }
    private static Credential ReadServiceCredential(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 16384) throw new InvalidDataException();
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            var credential = JsonConvert.DeserializeObject<Credential>(Encoding.UTF8.GetString(clear), JsonSettings);
            if (credential == null || string.IsNullOrWhiteSpace(credential.Username) || credential.Password == null) throw new InvalidDataException();
            return credential;
        }
        finally { Array.Clear(clear, 0, clear.Length); }
    }
}
