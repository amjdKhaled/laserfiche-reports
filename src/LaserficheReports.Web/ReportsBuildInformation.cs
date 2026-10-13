using System.Text.Json;

namespace LaserficheReports.Web;

internal static class ReportsBuildInformation
{
    internal static object Read(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "build-info.json");
        if (!File.Exists(path)) return new { Version = "development", SourceCommit = "unknown" };
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.Clone();
    }
}
