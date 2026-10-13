using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace LaserficheReports.Setup
{
    public static class SafetyActions
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LaserficheReports");

        [CustomAction]
        public static ActionResult ValidateIsolation(Session session)
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LaserficheReports");
                var expected = new Dictionary<string, string> { { "INSTALLFOLDER", root }, { "DATAROOT", DataRoot },
                    { "STARTMENUFOLDER", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Laserfiche Reports") } };
                var directories = new Dictionary<string, Tuple<string, string>>();
                using (var view = session.Database.OpenView("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`"))
                {
                    view.Execute(); Record record;
                    while ((record = view.Fetch()) != null)
                        using (record) directories[record.GetString(1)] = Tuple.Create(record.GetString(2), record.GetString(3));
                }
                bool added;
                do
                {
                    added = false;
                    foreach (var item in directories)
                    {
                        if (expected.ContainsKey(item.Key) || !expected.ContainsKey(item.Value.Item1)) continue;
                        var name = LongName(item.Value.Item2.Split(':')[0]);
                        expected[item.Key] = name == "." ? expected[item.Value.Item1] : Path.Combine(expected[item.Value.Item1], name);
                        added = true;
                    }
                } while (added);
                foreach (var item in expected) InstallerFileSafety.RequireSamePath(session.GetTargetPath(item.Key), item.Value);
                using (var view = session.Database.OpenView("SELECT `File`.`FileName`, `Component`.`Directory_` FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component`"))
                {
                    view.Execute(); Record record;
                    while ((record = view.Fetch()) != null)
                        using (record)
                        {
                            var directory = record.GetString(2);
                            if (!expected.ContainsKey(directory)) throw new IOException("Payload outside Reports directories.");
                            InstallerFileSafety.EnsureNoReparsePoints(Path.Combine(expected[directory], LongName(record.GetString(1))));
                        }
                }
                using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LaserficheReports"))
                {
                    var image = service?.GetValue("ImagePath") as string;
                    if (!string.IsNullOrEmpty(image) && !string.Equals(image.Trim('"'), Path.Combine(root, "LaserficheReports.Service.exe"), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The Reports service name is owned by another executable.");
                }
                var removing = session["REMOVE"].Split(',').Contains("ALL");
                if (!removing)
                {
                    using (var tasks = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree"))
                        if (tasks != null && tasks.GetSubKeyNames().Any(n => n.StartsWith("LaserficheReports-", StringComparison.Ordinal)))
                            throw new IOException("Stop the legacy Reports installation using deployment\\windows\\unregister.ps1 before migrating. Settings are preserved; never run both hosts.");
                    if (!string.IsNullOrWhiteSpace(session["REPORTS_PROFILE"])) ValidateProfile(session["REPORTS_PROFILE"]);
                    else if (!File.Exists(Path.Combine(DataRoot, "hosting.config.json")))
                        throw new IOException("Connection settings are required. Run LaserficheReports-Setup.exe.");
                }
                session["REPORTS_TRANSACTION"] = Guid.NewGuid().ToString("N");
                return ActionResult.Success;
            }
            catch (Exception error) { session.Log("Reports isolation rejected: {0}", error.Message); return ActionResult.Failure; }
        }

        internal static Dictionary<string, object> ValidateProfile(string encoded)
        {
            if (encoded.Length > 16384) throw new ArgumentException("Profile too large.");
            var profile = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var allowed = new[] { "ServerUrl", "ApiBasePath", "ApiVersion", "RepositoryId", "WebClientBaseUrl", "Port", "GraphPort", "Model", "OllamaUrl" };
            if (profile == null || profile.Keys.Except(allowed).Any()) throw new ArgumentException("Unsupported setting in profile.");
            Uri server;
            if (!Uri.TryCreate(Text(profile, "ServerUrl"), UriKind.Absolute, out server) || (server.Scheme != "http" && server.Scheme != "https") || !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
                throw new ArgumentException("Enter a Laserfiche HTTP/HTTPS server URL without credentials.");
            var api = Text(profile, "ApiBasePath");
            if (!api.StartsWith("/", StringComparison.Ordinal) || api.Contains("..") || api.Contains("?") || api.Contains("#") || api.Contains("\\"))
                throw new ArgumentException("Invalid API base path.");
            if (!new[] { "Auto", "v1", "v2" }.Contains(Text(profile, "ApiVersion"))) throw new ArgumentException("Invalid API version.");
            var port = Port(profile, "Port"); var graphPort = Port(profile, "GraphPort");
            if (port == graphPort) throw new ArgumentException("Web and planner ports must differ.");
            Uri ollama;
            if (!Uri.TryCreate(Text(profile, "OllamaUrl"), UriKind.Absolute, out ollama) || ollama.Scheme != "http" || !ollama.IsLoopback || !string.IsNullOrEmpty(ollama.UserInfo) || !string.IsNullOrEmpty(ollama.Query) || !string.IsNullOrEmpty(ollama.Fragment))
                throw new ArgumentException("Ollama must use a local HTTP address.");
            var model = Text(profile, "Model");
            if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Any(char.IsControl)) throw new ArgumentException("Invalid model.");
            var repo = Text(profile, "RepositoryId");
            if (repo.Length > 256 || repo.Any(char.IsControl)) throw new ArgumentException("Invalid repository.");
            var web = Text(profile, "WebClientBaseUrl");
            Uri webUri;
            if (web.Length > 0 && (!Uri.TryCreate(web, UriKind.Absolute, out webUri) || (webUri.Scheme != "http" && webUri.Scheme != "https") || !string.IsNullOrEmpty(webUri.UserInfo)))
                throw new ArgumentException("Invalid Web Client URL.");
            return profile;
        }

        [CustomAction]
        public static ActionResult WriteConfiguration(Session session)
        {
            try
            {
                var data = session.CustomActionData;
                InstallerFileSafety.RequireSamePath(data["DataRoot"], DataRoot);
                Directory.CreateDirectory(DataRoot);
                if (string.IsNullOrWhiteSpace(data["Profile"])) return ActionResult.Success;
                var profile = ValidateProfile(data["Profile"]);
                var connectionPath = Path.Combine(DataRoot, "laserfiche.config.json");
                var hostingPath = Path.Combine(DataRoot, "hosting.config.json");
                var connection = ReadObject(connectionPath);
                var laserfiche = connection.TryGetValue("Laserfiche", out var lf) && lf is Dictionary<string, object> dictionary ? dictionary : new Dictionary<string, object>();
                foreach (var key in new[] { "ServerUrl", "ApiBasePath", "ApiVersion", "RepositoryId", "WebClientBaseUrl" })
                    if (profile.ContainsKey(key)) laserfiche[key] = profile[key];
                connection["Laserfiche"] = laserfiche;
                var hosting = ReadObject(hostingPath);
                foreach (var key in new[] { "Port", "GraphPort", "Model", "OllamaUrl" }) hosting[key] = profile[key];
                var writes = new[] { Tuple.Create(connectionPath, Json.Serialize(connection)), Tuple.Create(hostingPath, Json.Serialize(hosting)) };
                var journal = new Dictionary<string, object>();
                foreach (var item in writes)
                {
                    InstallerFileSafety.EnsureNoReparsePoints(item.Item1);
                    var bytes = Encoding.UTF8.GetBytes(item.Item2);
                    journal[item.Item1] = new Dictionary<string, object> { { "before", File.Exists(item.Item1) ? Convert.ToBase64String(File.ReadAllBytes(item.Item1)) : "" },
                        { "existed", File.Exists(item.Item1) }, { "afterHash", Hash(bytes) } };
                }
                InstallerFileSafety.WriteBytesAtomic(Journal(data["Transaction"]), Encoding.UTF8.GetBytes(Json.Serialize(journal)));
                foreach (var item in writes) InstallerFileSafety.WriteBytesAtomic(item.Item1, Encoding.UTF8.GetBytes(item.Item2));
                return ActionResult.Success;
            }
            catch (Exception error) { session.Log("Reports configuration failed: {0}", error.Message); return ActionResult.Failure; }
        }

        [CustomAction]
        public static ActionResult RollbackConfiguration(Session session)
        {
            try
            {
                var path = Journal(session.CustomActionData["Transaction"]);
                var journal = ReadObject(path);
                foreach (var item in journal)
                {
                    if (item.Key != Path.Combine(DataRoot, "laserfiche.config.json") && item.Key != Path.Combine(DataRoot, "hosting.config.json")) throw new IOException("Journal path outside allowlist.");
                    InstallerFileSafety.EnsureNoReparsePoints(item.Key);
                    var entry = (Dictionary<string, object>)item.Value;
                    // Preserve configuration changed concurrently by an administrator.
                    if (!File.Exists(item.Key) || Hash(File.ReadAllBytes(item.Key)) != Text(entry, "afterHash")) continue;
                    if ((bool)entry["existed"]) InstallerFileSafety.WriteBytesAtomic(item.Key, Convert.FromBase64String(Text(entry, "before")));
                    else File.Delete(item.Key);
                }
                if (File.Exists(path)) File.Delete(path);
                return ActionResult.Success;
            }
            catch (Exception error) { session.Log("Reports rollback failed: {0}", error.Message); return ActionResult.Failure; }
        }

        [CustomAction]
        public static ActionResult CommitConfiguration(Session session)
        {
            try { var path = Journal(session.CustomActionData["Transaction"]); if (File.Exists(path)) File.Delete(path); return ActionResult.Success; }
            catch (Exception error) { session.Log("Reports journal cleanup failed: {0}", error.Message); return ActionResult.Failure; }
        }

        private static string Journal(string transaction)
        {
            Guid id; if (!Guid.TryParseExact(transaction, "N", out id)) throw new ArgumentException("Invalid transaction.");
            var path = Path.Combine(DataRoot, ".install-" + id.ToString("N") + ".json");
            InstallerFileSafety.EnsureNoReparsePoints(path); return path;
        }
        private static Dictionary<string, object> ReadObject(string path)
        {
            InstallerFileSafety.EnsureNoReparsePoints(path);
            return File.Exists(path) ? Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)) : new Dictionary<string, object>();
        }
        private static string LongName(string value) => value.Contains("|") ? value.Substring(value.IndexOf('|') + 1) : value;
        private static string Text(Dictionary<string, object> value, string key) => value.TryGetValue(key, out var item) ? Convert.ToString(item, CultureInfo.InvariantCulture) ?? "" : "";
        private static int Port(Dictionary<string, object> value, string key)
        {
            int port; if (!int.TryParse(Text(value, key), out port) || port < 1024 || port > 65535) throw new ArgumentException("Invalid port."); return port;
        }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes)); }
    }
}
